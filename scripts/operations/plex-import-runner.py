#!/usr/bin/env python3
import csv
import fcntl
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import time
import urllib.error
import urllib.request
import zipfile
from pathlib import Path

from existing_id_imports import id_target, skip_existing

HOST = "http://192.168.20.65:8080"
BASE = Path("/mnt/nzbdav-cache3/infinidysk-migration-artifacts/mapped-20260924T214816Z/plex")
BATCHES = BASE / "batches-20260927"
INPUT = Path("/opt/infinidysk-migration/input")
INVENTORY = BASE / "current-inventory-20260927-gc4"
LIBRARY_ROOT = "/mnt/plex"
TARGET_LIBRARY = "/mnt/plex2"
TARGET_ROOT = "/mnt/remote/infinidysk"
TOOL = "/opt/infinidysk-migration/tools/current/NzbDavMigration"
PROMOTE_TOOL = "/opt/infinidysk-migration/promote-links.py"
PROMOTION_DIR = BASE / "live-promotion-20260927"
ENV_FILE = "/opt/infinidysk-migration/canary/phase-b-20260920T184834Z/legacy-readonly.env"
MASTER = "84dea1d8d156eff93b4035c7bbb3a1b4a09d09e1dcf04004efe1199c6a9c94a6"
SOURCE_COUNT = 41324
RECOVERABLE_COUNT = 26265
BATCH_COUNT = 92
MAX_QUEUE_DEPTH = 5
SUBMIT_WORKERS = 1
REPORTS = BASE / "import-reports"
JOURNAL_DIR = BASE / "journals"
POLL_SECONDS = 15


def log(message):
    line = f"{time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime())} {message}"
    print(line, flush=True)
    REPORTS.mkdir(parents=True, exist_ok=True)
    with (REPORTS / "plex-import-runner.log").open("a", encoding="utf-8") as stream:
        stream.write(line + "\n")


def api_key():
    sql = 'SELECT "ConfigValue" FROM "ConfigItems" WHERE "ConfigName" = $$api.key$$ LIMIT 1;\n'
    result = subprocess.run(
        ["docker", "exec", "-i", "infinidysk-postgres", "sh", "-c",
         'psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -At'],
        input=sql, text=True, capture_output=True, check=True)
    key = result.stdout.strip()
    if not key:
        raise RuntimeError("Could not read the InfiniDysk API key from its config database")
    return key


KEY = api_key()


def request(path, method="GET", body=None, timeout=3600, accepted=(200,)):
    data = None if body is None else json.dumps(body, separators=(",", ":")).encode()
    headers = {"x-api-key": KEY, "Accept": "application/json"}
    if data is not None:
        headers["Content-Type"] = "application/json"
    req = urllib.request.Request(HOST + path, data=data, headers=headers, method=method)
    try:
        with urllib.request.urlopen(req, timeout=timeout) as response:
            code = response.status
            raw = response.read()
    except urllib.error.HTTPError as error:
        code = error.code
        raw = error.read()
    if code not in accepted:
        raise RuntimeError(f"{method} {path} returned HTTP {code}: {raw[:1200].decode(errors='replace')}")
    if not raw:
        return None
    return json.loads(raw)


def current_status():
    return request("/api/migration/nzbdav/status")


def wait_session(expected_states, batch_index, label):
    last = None
    while True:
        state = current_status()
        current = state["sessionStatus"]
        if state.get("sourcePackageRoot", "").endswith(f"batch-{batch_index + 1:04d}") is False:
            raise RuntimeError(f"Active package does not match batch index {batch_index}: {state.get('sourcePackageRoot')}")
        counts = tuple((x.get("state"), x.get("count")) for x in state.get("submissions", []))
        snapshot = (current, counts)
        if snapshot != last:
            log(f"batch {batch_index + 1}/{BATCH_COUNT} {label}: {current} submissions={counts}")
            last = snapshot
        if current in expected_states:
            return state
        if current in ("failed", "cancelled", "paused", "scan_cancelling", "cancelling"):
            raise RuntimeError(f"batch {batch_index + 1} stopped in session state {current}")
        time.sleep(POLL_SECONDS)


def stage_batch(batch_index):
    source = BATCHES / f"batch-{batch_index + 1:04d}"
    stage_name = f"plex-full-20260927-batch-{batch_index + 1:04d}"
    destination = INPUT / stage_name
    if not (source / "manifest.json").is_file() or not (source / "SHA256SUMS").is_file():
        raise RuntimeError(f"exported package is incomplete: {source}")
    src_sums = hashlib.sha256((source / "SHA256SUMS").read_bytes()).hexdigest()
    if destination.exists():
        dst_sums = destination / "SHA256SUMS"
        if not dst_sums.is_file() or hashlib.sha256(dst_sums.read_bytes()).hexdigest() != src_sums:
            raise RuntimeError(f"staged package does not match its NAS source: {destination}")
    else:
        free = shutil.disk_usage(INPUT).free
        if free < 8 * 1024**3:
            raise RuntimeError(f"less than 8 GiB free before staging {source}: {free} bytes")
        shutil.copytree(source, destination, symlinks=False)
    for path in [destination, *destination.rglob("*")]:
        if path.is_dir():
            path.chmod(0o755)
        elif path.is_file():
            path.chmod(0o644)
    checked = subprocess.run(["sha256sum", "-c", "SHA256SUMS"], cwd=destination,
                             capture_output=True, text=True)
    if checked.returncode:
        raise RuntimeError(f"staged batch {batch_index + 1} failed SHA256SUMS: {checked.stderr[-2000:]}")
    manifest = json.loads((destination / "manifest.json").read_text())
    if manifest.get("masterManifestDigest") != MASTER or manifest.get("batchIndex") != batch_index:
        raise RuntimeError(f"staged batch {batch_index + 1} master/index mismatch")
    return stage_name, destination, manifest


def ensure_scan_and_run(batch_index, stage_name, destination, manifest):
    state = current_status()
    expected_root = f"/config/migration-input/{stage_name}"
    if state.get("sourcePackageRoot") != expected_root:
        if state["sessionStatus"] not in ("idle", "connected", "mapped", "scanned", "complete", "cancelled", "linked"):
            raise RuntimeError(f"refusing to replace active session {state['sessionStatus']}")
        result = request("/api/migration/nzbdav/full/connect", "POST", {
            "packagePath": expected_root,
            "masterManifestDigest": MASTER,
            "sourceLinkCount": SOURCE_COUNT,
            "recoverableCount": RECOVERABLE_COUNT,
            "maxQueueDepth": MAX_QUEUE_DEPTH,
            "submitWorkers": SUBMIT_WORKERS,
        })
        log(f"connected batch {batch_index + 1}: selected={result['selectionCount']} coverage={result['coverage']:.6%}")
        categories = sorted({link["libraryRelativePath"].split("/")[0]
                            for link in manifest["selectedLinks"]})
        request("/api/migration/nzbdav/categories", "PUT", {"mappings": [
            {"altmountCategory": category, "targetCategory": "migration-plex", "action": "migrate"}
            for category in categories]})
        request("/api/migration/nzbdav/scan", "POST", {})
        wait_session({"scanned"}, batch_index, "scan")
    else:
        if state["sessionStatus"] in ("connected", "mapped"):
            request("/api/migration/nzbdav/categories", "PUT", {"mappings": [
                {"altmountCategory": category, "targetCategory": "migration-plex", "action": "migrate"}
                for category in sorted({link["libraryRelativePath"].split("/")[0]
                                        for link in manifest["selectedLinks"]})]})
            request("/api/migration/nzbdav/scan", "POST", {})
            wait_session({"scanned"}, batch_index, "scan")
        elif state["sessionStatus"] == "scanning":
            wait_session({"scanned"}, batch_index, "scan")
    state = current_status()
    if state["sessionStatus"] in ("scanned", "paused"):
        skipped = skip_existing(
            "/opt/infinidysk/config/usenet-migration.db", manifest,
            LIBRARY_ROOT, TARGET_ROOT, expected_root,
            REPORTS / f"batch-{batch_index + 1:04d}" / "already-infinidysk.json")
        log(f"batch {batch_index + 1}: skipped {len(skipped)} already-InfiniDysk library files")
    if state["sessionStatus"] == "scanned":
        digest = hashlib.sha256((destination / "SHA256SUMS").read_bytes()).hexdigest()
        request("/api/migration/nzbdav/run", "POST", {
            "packageDigest": digest,
            "selectionCount": len(manifest["selectedLinks"]),
        })
    elif state["sessionStatus"] == "paused":
        request("/api/migration/nzbdav/run/resume", "POST", {})
    return wait_session({"complete"}, batch_index, "import")


def run_tool(service, args, allow_failure=False):
    command = ["systemd-run", "--wait", "--pipe", "--collect", f"--unit={service}",
               f"--property=EnvironmentFile={ENV_FILE}", "--property=CPUQuota=50%",
               "--property=Nice=19", "--property=IOWeight=10"]
    command.extend((TOOL, *args))
    result = subprocess.run(command, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                            timeout=8 * 3600)
    if result.returncode and not allow_failure:
        raise RuntimeError(f"{service} failed ({result.returncode}): {result.stdout[-3000:]}")
    log(f"{service} complete (exit={result.returncode}): {result.stdout.strip()[-500:]}")
    return result


def save_json(path, value):
    tmp = path.with_suffix(path.suffix + ".tmp")
    tmp.write_text(json.dumps(value, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    tmp.replace(path)


def plan_zip_and_extract(batch_index, report_dir):
    plan_dir = report_dir / "plan"
    plan_path = plan_dir / "plan.json"
    sums_path = plan_dir / "SHA256SUMS"
    if not (plan_path.is_file() and sums_path.is_file()):
        if plan_path.exists() or sums_path.exists():
            raise RuntimeError("Existing canary plan is incomplete; refusing to replace it")
        request("/api/migration/nzbdav/canary-plan", "POST", {})
        req = urllib.request.Request(HOST + "/api/migration/nzbdav/canary-plan",
                                     headers={"x-api-key": KEY})
        with urllib.request.urlopen(req, timeout=3600) as response:
            archive = response.read()
        zip_path = report_dir / "canary-plan.zip"
        zip_path.write_bytes(archive)
        plan_dir.mkdir(exist_ok=True)
        with zipfile.ZipFile(zip_path) as bundle:
            allowed = {"plan.json", "SHA256SUMS"}
            if set(bundle.namelist()) != allowed:
                raise RuntimeError(f"unexpected plan archive entries: {bundle.namelist()}")
            for name in allowed:
                (plan_dir / name).write_bytes(bundle.read(name))
    check = subprocess.run(["sha256sum", "-c", "SHA256SUMS"], cwd=plan_dir,
                           capture_output=True, text=True)
    if check.returncode:
        raise RuntimeError(f"canary plan checksum failed: {check.stderr}")
    return plan_path


def record_batch_outcomes(batch_index, correlation, failures):
    report_dir = REPORTS / f"batch-{batch_index + 1:04d}"
    report_dir.mkdir(parents=True, exist_ok=True)
    save_json(report_dir / "correlation.json", correlation)
    save_json(report_dir / "import-failures.json", failures)
    rows = []
    fail_by_id = {str(x.get("legacyDavItemId")): x for x in failures.get("failures", [])}
    for row in correlation.get("rows", []):
        status = row.get("correlationStatus")
        if status == "exact":
            continue
        item_id = str(row.get("legacyDavItemId", ""))
        if status == "import-failed":
            failure = fail_by_id.get(item_id, {})
            reason = failure.get("reason", "terminal import failure")
            # InfiniDysk regrabs damaged/missing-article failures through Sonarr/Radarr (#134):
            # it removes the broken library symlink and the Arr file record, then searches.
            if failure.get("regrabStatus"):
                status = failure["regrabStatus"]
                detail = "; ".join(x for x in (failure.get("regrabArrTarget"), failure.get("regrabMessage")) if x)
                reason = f"{reason} -> {detail}" if detail else reason
        elif status == "scan-excluded":
            reason = "Recorded scan exclusion; existing InfiniDysk links are preserved"
        else:
            reason = "No exact target file matched this imported NZB item"
        rows.append({"batchIndex": batch_index, "libraryRelativePath": row.get("libraryRelativePath", ""),
                     "legacyDavItemId": item_id, "status": status or "unknown", "reason": reason})
    with (report_dir / "not-imported.csv").open("w", newline="", encoding="utf-8") as stream:
        fields = ["batchIndex", "libraryRelativePath", "legacyDavItemId", "status", "reason"]
        writer = csv.DictWriter(stream, fieldnames=fields)
        writer.writeheader()
        writer.writerows(rows)
    return report_dir, rows


def write_json_once(path, value):
    if path.exists():
        if json.loads(path.read_text()) != value:
            raise RuntimeError(f"existing migration evidence differs: {path}")
        return
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(fd, "w", encoding="utf-8") as stream:
        json.dump(value, stream, ensure_ascii=False, indent=2)
        stream.write("\n")
        stream.flush()
        os.fsync(stream.fileno())


def retry_failed_validations(batch_index, journal, original_path):
    resolved_path = JOURNAL_DIR / f"batch-{batch_index + 1:04d}-resolved.json"
    if resolved_path.exists():
        return json.loads(resolved_path.read_text()), resolved_path
    results = json.loads(original_path.read_text())
    initial_paths = [row["libraryRelativePath"] for row in results]
    if len(initial_paths) != len(set(initial_paths)):
        raise RuntimeError("validation report has duplicate library paths")
    for attempt in range(1, 3):
        failures = [row for row in results if row.get("success") is not True]
        if not failures:
            break
        retry_journal = JOURNAL_DIR / f"batch-{batch_index + 1:04d}-retry-{attempt}.json"
        retry_result = JOURNAL_DIR / f"batch-{batch_index + 1:04d}-retry-{attempt}-validated.json"
        source = json.loads(journal.read_text())
        failed_paths = {row["libraryRelativePath"] for row in failures}
        source["links"] = [row for row in source["links"]
                           if row["libraryRelativePath"] in failed_paths]
        if len(source["links"]) != len(failed_paths):
            raise RuntimeError("validation retry does not match the applied journal")
        write_json_once(retry_journal, source)
        if not retry_result.exists():
            run_tool(f"id64-plex-retry-{batch_index + 1:04d}-{attempt}-20260928", [
                "validate-links", "--journal", str(retry_journal), "--output", str(retry_result),
                "--max-read-bytes", "65536", "--timeout-seconds", "20"], allow_failure=True)
        if not retry_result.exists():
            raise RuntimeError("validation retry produced no durable result")
        by_path = {row["libraryRelativePath"]: row for row in json.loads(retry_result.read_text())}
        if set(by_path) != failed_paths:
            raise RuntimeError("validation retry results disagree with requested paths")
        results = [by_path[row["libraryRelativePath"]]
                   if row["libraryRelativePath"] in by_path else row for row in results]
        if attempt < 2 and any(row.get("success") is not True for row in results):
            time.sleep(30)
    write_json_once(resolved_path, results)
    return results, resolved_path


def regrab_unreadable_links(batch_index, package, manifest, report_dir, failures):
    """Regrab links whose new InfiniDysk copy failed bounded-read validation.

    The old library link (under LIBRARY_ROOT) still points at the legacy item because
    promotion excludes these paths. Instead of deleting the legacy NzbDav item, ask
    InfiniDysk to regrab through Sonarr/Radarr: it removes only that symlink (journaled)
    and the Arr file record, then searches (#134). Each link is requested independently and
    its outcome recorded, so a slow or refusing Arr never stalls the batch.
    """
    selected = {row["libraryRelativePath"]: row for row in manifest["selectedLinks"]}
    release_by_id = {leaf["legacyDavItemId"]: release["sourceReleaseId"]
                     for release in manifest["releases"] for leaf in release["leaves"]}
    rows = []
    for failure in failures:
        path = failure["libraryRelativePath"]
        if path not in selected or not failure.get("error"):
            raise RuntimeError("unreadable link lacks selected path or error evidence")
        item_id = selected[path]["legacyDavItemId"]
        rows.append({"sourceReleaseId": release_by_id[item_id], "legacyDavItemId": item_id,
                     "libraryRelativePath": path, "submissionState": "failed",
                     "reason": failure["error"]})
    report = {"status": True, "packageDigest": hashlib.sha256((package / "SHA256SUMS").read_bytes()).hexdigest(),
              "batchIndex": batch_index, "failedCount": len(rows), "failures": rows}
    write_json_once(report_dir / "validation-cleanup-failures.json", report)
    outcomes_path = report_dir / "validation-regrab-outcomes.json"
    outcomes = json.loads(outcomes_path.read_text()) if outcomes_path.exists() else {}
    for row in rows:
        path = row["libraryRelativePath"]
        if path in outcomes:
            continue
        body = {"linkPath": os.path.join(LIBRARY_ROOT, path), "source": "migration",
                "reason": f"migration validation failed: {row['reason']}"[:300]}
        try:
            result = request("/api/arr-regrab", "POST", body, timeout=300, accepted=(200, 404, 409)) or {}
            regrab = result.get("request") or {}
            status = f"regrab-{regrab['status']}" if regrab.get("status") else (
                "regrab-requested" if result.get("status") else "regrab-skipped")
            message = result.get("message") or result.get("error") or ""
        except Exception as error:  # a failed request is recorded, never fatal to the batch
            status, message = "regrab-error", str(error)[:300]
        outcomes[path] = {"status": status, "message": message}
        outcomes_path.write_text(json.dumps(outcomes, indent=2, sort_keys=True) + "\n")
        log(f"batch {batch_index + 1} unreadable link {path}: {status} {message}".rstrip())
    write_json_once(report_dir / "validation-failures.json", failures)
    skipped_csv = report_dir / "validation-not-imported.csv"
    if not skipped_csv.exists():
        with skipped_csv.open("x", newline="", encoding="utf-8") as stream:
            writer = csv.DictWriter(stream, fieldnames=["batchIndex", "libraryRelativePath",
                                                       "legacyDavItemId", "status", "reason"])
            writer.writeheader()
            for row in rows:
                outcome = outcomes.get(row["libraryRelativePath"], {})
                detail = outcome.get("message")
                writer.writerow({"batchIndex": batch_index,
                                 "libraryRelativePath": row["libraryRelativePath"],
                                 "legacyDavItemId": row["legacyDavItemId"],
                                 "status": outcome.get("status", "validation-failed"),
                                 "reason": f"{row['reason']} -> {detail}" if detail else row["reason"]})
    log(f"batch {batch_index + 1} unreadable links handed to regrab: {len(rows)}")
    return [row["legacyDavItemId"] for row in rows]


def filtered_journal(batch_index, journal, excluded_paths):
    source = json.loads(journal.read_text())
    original_count = len(source["links"])
    source["links"] = [row for row in source["links"]
                       if row["libraryRelativePath"] not in excluded_paths]
    if original_count - len(source["links"]) != len(excluded_paths):
        raise RuntimeError("promotion exclusion does not match applied journal")
    output = JOURNAL_DIR / f"batch-{batch_index + 1:04d}-promotable.json"
    write_json_once(output, source)
    return output


def verify_existing_links(batch_index):
    existing_report = REPORTS / f"batch-{batch_index + 1:04d}" / "already-infinidysk.json"
    if existing_report.exists():
        evidence = json.loads(existing_report.read_text())
        for link in evidence["skippedLinks"]:
            relative = link["libraryRelativePath"]
            if id_target(LIBRARY_ROOT, relative, TARGET_ROOT) != evidence["observedTargets"][relative]:
                raise RuntimeError("Previously skipped InfiniDysk library link changed; reconcile before resuming")


def process_batch(batch_index, stage_name, destination, manifest):
    verify_existing_links(batch_index)
    request("/api/migration/nzbdav/reconcile", "POST", {}, timeout=3600)
    report_dir, excluded = record_batch_outcomes(
        batch_index,
        request("/api/migration/nzbdav/correlation", timeout=3600),
        request("/api/migration/nzbdav/import-failures", timeout=3600))
    # Failed imports are no longer cleaned up by deleting the legacy NzbDav item; InfiniDysk
    # queues a Sonarr/Radarr regrab for them instead (recorded in not-imported.csv above).
    plan_path = plan_zip_and_extract(batch_index, report_dir)
    digest = hashlib.sha256(plan_path.read_bytes()).hexdigest()
    plan = json.loads(plan_path.read_text())
    exact_count = sum(1 for link in plan.get("links", [])
                      if link.get("correlationStatus") == "exact" and link.get("applyStatus") == "planned")
    if exact_count != plan.get("actionableCount"):
        raise RuntimeError(f"plan actionable count mismatch: {exact_count} != {plan.get('actionableCount')}")
    journal = JOURNAL_DIR / f"batch-{batch_index + 1:04d}.json"
    validated_path = JOURNAL_DIR / f"batch-{batch_index + 1:04d}-validated.json"
    JOURNAL_DIR.mkdir(parents=True, exist_ok=True)
    prior = json.loads(journal.read_text()) if journal.exists() else {"links": []}
    prior_applied = {row["libraryRelativePath"] for row in prior["links"]
                     if row.get("status") == "applied"}
    pending_links = [row for row in plan["links"]
                     if row.get("correlationStatus") == "exact"
                     and row.get("applyStatus") == "planned"
                     and row["libraryRelativePath"] not in prior_applied]
    missing_paths = [row["libraryRelativePath"] for row in pending_links
                     if not os.path.lexists(os.path.join(LIBRARY_ROOT, row["libraryRelativePath"]))]
    replaced_paths = {}
    for row in pending_links:
        source = os.path.join(LIBRARY_ROOT, row["libraryRelativePath"])
        if not os.path.islink(source):
            continue
        current_target = os.readlink(source)
        if current_target != row["originalLegacyTarget"] and id_target(LIBRARY_ROOT, row["libraryRelativePath"], TARGET_ROOT) is not None:
            replaced_paths[row["libraryRelativePath"]] = current_target
    missing_file = report_dir / "missing-source-paths.json"
    if missing_paths:
        write_json_once(missing_file, missing_paths)
    replaced_file = report_dir / "replaced-source-paths.json"
    if replaced_paths:
        write_json_once(replaced_file, replaced_paths)
    if not journal.exists() or len(prior_applied) + len(missing_paths) + len(replaced_paths) != exact_count:
        run_tool(f"id64-plex-apply-{batch_index + 1:04d}-20260927", [
            "apply-sharded-links", "--plan", str(plan_path), "--mapped-inventory", str(INVENTORY),
            "--source-root", LIBRARY_ROOT, "--library-root", TARGET_LIBRARY,
            "--target-root", TARGET_ROOT, "--journal", str(journal),
            *(["--missing-source-paths", str(missing_file)] if missing_paths else []),
            *(["--replaced-source-paths", str(replaced_file)] if replaced_paths else [])])
    journal_data = json.loads(journal.read_text())
    if journal_data.get("planSha256") != digest or journal_data.get("sourceRoot") != LIBRARY_ROOT \
            or journal_data.get("libraryRoot") != TARGET_LIBRARY \
            or journal_data.get("targetRoot") != TARGET_ROOT:
        raise RuntimeError("persisted apply journal does not match this plan and library roots")
    applied = sum(1 for item in journal_data.get("links", []) if item.get("status") == "applied")
    missing = [item for item in journal_data.get("links", [])
               if item.get("status") == "source-missing"]
    replaced = [item for item in journal_data.get("links", [])
                if item.get("status") == "source-replaced"]
    if applied + len(missing) + len(replaced) != exact_count or len(journal_data.get("links", [])) != exact_count:
        raise RuntimeError(f"batch {batch_index + 1} accounted for {applied + len(missing) + len(replaced)} of {exact_count} exact links")
    missing_by_path = {row["libraryRelativePath"]: row["legacyDavItemId"]
                       for row in plan["links"] if row.get("applyStatus") == "planned"}
    missing_ids = [missing_by_path[row["libraryRelativePath"]] for row in missing]
    replaced_ids = [missing_by_path[row["libraryRelativePath"]] for row in replaced]
    if not validated_path.exists():
        run_tool(f"id64-plex-validate-{batch_index + 1:04d}-20260927", [
            "validate-links", "--journal", str(journal), "--output", str(validated_path),
            "--max-read-bytes", "65536", "--timeout-seconds", "20"], allow_failure=True)
    if not validated_path.exists():
        raise RuntimeError(f"batch {batch_index + 1} validation produced no durable result")
    validations, resolved_path = retry_failed_validations(batch_index, journal, validated_path)
    validated = sum(1 for item in validations if item.get("success") is True)
    failures_validation = [item for item in validations if item.get("success") is not True]
    if validated + len(failures_validation) != applied:
        raise RuntimeError(f"batch {batch_index + 1} validation coverage disagrees with the exact plan")
    validation_rejects = []
    excluded_paths = {row["libraryRelativePath"] for row in missing + replaced}
    if failures_validation:
        validation_rejects = regrab_unreadable_links(
            batch_index, destination, manifest, report_dir, failures_validation)
        excluded_paths.update(row["libraryRelativePath"] for row in failures_validation)
    promotion_journal = filtered_journal(batch_index, journal, excluded_paths) if excluded_paths else journal
    if missing:
        with (report_dir / "missing-source-not-imported.csv").open("w", newline="", encoding="utf-8") as stream:
            writer = csv.DictWriter(stream, fieldnames=["batchIndex", "libraryRelativePath",
                                                      "legacyDavItemId", "status", "reason"])
            writer.writeheader()
            for row in missing:
                writer.writerow({"batchIndex": batch_index,
                                 "libraryRelativePath": row["libraryRelativePath"],
                                 "legacyDavItemId": missing_by_path[row["libraryRelativePath"]],
                                 "status": "source-missing",
                                 "reason": "Source library link disappeared after the immutable plan was created"})
    if replaced:
        with (report_dir / "replaced-source-not-imported.csv").open("w", newline="", encoding="utf-8") as stream:
            writer = csv.DictWriter(stream, fieldnames=["batchIndex", "libraryRelativePath",
                                                       "legacyDavItemId", "status", "reason"])
            writer.writeheader()
            for row in replaced:
                writer.writerow({"batchIndex": batch_index,
                                 "libraryRelativePath": row["libraryRelativePath"],
                                 "legacyDavItemId": missing_by_path[row["libraryRelativePath"]],
                                 "status": "source-replaced",
                                 "reason": "Source library link was replaced after the immutable plan was created"})
    if validated:
        PROMOTION_DIR.mkdir(parents=True, exist_ok=True)
        promotion_record = PROMOTION_DIR / f"batch-{batch_index + 1:04d}.json"
        if not promotion_record.exists():
            prepared = subprocess.run(
                ["python3", PROMOTE_TOOL, "--prepare", "--record", str(promotion_record),
                 "--journal", str(promotion_journal)], text=True, capture_output=True, timeout=3600)
            if prepared.returncode:
                raise RuntimeError(f"batch {batch_index + 1} live promotion preflight failed: {prepared.stderr[-2000:]}")
            log(prepared.stdout.strip())
        promoted = subprocess.run(
            ["python3", PROMOTE_TOOL, "--apply", "--record", str(promotion_record)],
            text=True, capture_output=True, timeout=3600)
        if promoted.returncode:
            raise RuntimeError(f"batch {batch_index + 1} live promotion failed: {promoted.stderr[-2000:]}")
        promotion_counts = json.loads(promoted.stdout.strip())
        if promotion_counts["changed"] + promotion_counts["already"] != validated:
            raise RuntimeError(f"batch {batch_index + 1} live promotion did not cover {validated} links")
        log(f"batch {batch_index + 1}/{BATCH_COUNT} live links promoted: {validated}")
    for row in missing:
        relative = row["libraryRelativePath"]
        if os.path.lexists(os.path.join(LIBRARY_ROOT, relative)) or os.path.lexists(
                os.path.join(TARGET_LIBRARY, relative)):
            raise RuntimeError(f"missing source evidence changed before acknowledgement: {relative}")
    for row in replaced:
        relative = row["libraryRelativePath"]
        source = os.path.join(LIBRARY_ROOT, relative)
        if not os.path.islink(source) or os.readlink(source) != row["replacementSourceTarget"] \
                or os.path.lexists(os.path.join(TARGET_LIBRARY, relative)):
            raise RuntimeError(f"replaced source evidence changed before acknowledgement: {relative}")
    verify_existing_links(batch_index)
    ack = request(f"/api/migration/nzbdav/full/batches/{batch_index}/acknowledge-plan", "POST", {
        "planDigest": digest, "appliedCount": applied, "validatedCount": validated,
        "unvalidatedExactSourceIds": validation_rejects, "missingSourceIds": missing_ids,
        "replacedSourceIds": replaced_ids})
    save_json(report_dir / "acknowledgement.json", ack)
    log(f"batch {batch_index + 1}/{BATCH_COUNT} acknowledged: selected={len(manifest['selectedLinks'])}, validated={validated}, unreadable={len(validation_rejects)}, missing-source={len(missing)}, replaced-source={len(replaced)}, not-imported={len(excluded) + len(validation_rejects) + len(missing) + len(replaced)}")
    shutil.rmtree(destination)


def write_final_report():
    output = REPORTS / "not-imported-plex-20260927.csv"
    rows = []
    def add(path, status_override, reason_field="reason"):
        if not path.exists():
            return
        with path.open(newline="", encoding="utf-8-sig") as stream:
            for src in csv.DictReader(stream):
                rows.append({"batchIndex": src.get("batchIndex", ""),
                             "libraryRelativePath": src.get("libraryRelativePath", ""),
                             "legacyDavItemId": src.get("legacyDavItemId", ""),
                             "status": status_override or src.get("status", "unknown"),
                             "reason": src.get(reason_field, "")})
    add(REPORTS / "recovery-reuse-exclusions-20260927.csv", "not-recovered-from-sealed-recovery")
    add(REPORTS / "current-not-imported-source-drift-20260927.csv", "source-drift")
    add(REPORTS / "noncanonical-jobname-exclusions-20260927.csv", "noncanonical-or-mixed-legacy-job-name")
    add(REPORTS / "batch-0001" / "not-imported.csv", None)
    for index in range(1, BATCH_COUNT):
        add(REPORTS / f"batch-{index + 1:04d}" / "not-imported.csv", None)
    for index in range(BATCH_COUNT):
        add(REPORTS / f"batch-{index + 1:04d}" / "validation-not-imported.csv", None)
        add(REPORTS / f"batch-{index + 1:04d}" / "missing-source-not-imported.csv", None)
        add(REPORTS / f"batch-{index + 1:04d}" / "replaced-source-not-imported.csv", None)
    tmp = output.with_suffix(".csv.tmp")
    with tmp.open("w", newline="", encoding="utf-8") as stream:
        fields = ["batchIndex", "libraryRelativePath", "legacyDavItemId", "status", "reason"]
        writer = csv.DictWriter(stream, fieldnames=fields)
        writer.writeheader()
        writer.writerows(rows)
    tmp.replace(output)
    log(f"cumulative non-imported report: {output} rows={len(rows)}")


def reconstruct_first_batch_report():
    report_dir = REPORTS / "batch-0001"
    csv_path = report_dir / "not-imported.csv"
    if csv_path.exists():
        return
    report_dir.mkdir(parents=True, exist_ok=True)
    package = json.loads((BATCHES / "batch-0001" / "manifest.json").read_text())
    first_plan = json.loads((REPORTS / "batch-0001-plan" / "plan.json").read_text())
    exact = {str(link.get("legacyDavItemId")) for link in first_plan.get("links", [])}
    failed = {
        "5fd884be-8e8a-fb5d-9d9f-b484ce35e1f6": "Failed to parse RAR volume headers: Rar signature not found",
        "c66e681a-a4a6-da5f-aeed-0d0bd2113b0f": "Missing articles: important file has missing segments across all providers (pGE6dzqoz2JAVuNIL.part06.rar)",
        "8d7a02d4-3179-8858-b2c7-796afc2097e9": "Missing articles: important file has missing segments across all providers (eNyOiFacLbgNUbFif.part06.rar)",
        "5fc04187-815e-0250-a0bd-1857090a856b": "Missing articles: important file has missing segments across all providers (MobLand.S01E02.Jigsaw.Puzzle.1080p.AMZN.WEB-DL.DDP5.1.H.264-RAWR.mkv)",
        "1036c71e-623e-145d-827a-b624352bb7f1": "Missing articles: important file has missing segments across all providers (S8Tg1726lkny.mkv)",
    }
    rows = []
    correlation_rows = []
    for item in package.get("selectedLinks", []):
        item_id = str(item.get("legacyDavItemId", ""))
        if item_id in exact:
            status, reason = "exact", ""
        elif item_id in failed:
            status, reason = "import-failed", failed[item_id]
            rows.append({"batchIndex": 0, "libraryRelativePath": item.get("libraryRelativePath", ""),
                         "legacyDavItemId": item_id, "status": status, "reason": reason})
        else:
            status, reason = "unmatched-target", "No exact target file matched this imported NZB item"
            rows.append({"batchIndex": 0, "libraryRelativePath": item.get("libraryRelativePath", ""),
                         "legacyDavItemId": item_id, "status": status, "reason": reason})
        correlation_rows.append({"legacyDavItemId": item_id,
                                 "libraryRelativePath": item.get("libraryRelativePath", ""),
                                 "correlationStatus": status})
    save_json(report_dir / "correlation.json", {"selectedCount": len(correlation_rows),
              "exactCount": len(exact), "rows": correlation_rows})
    save_json(report_dir / "import-failures.json", {"failedCount": len(failed), "failures": [
        {"legacyDavItemId": item_id, "reason": reason} for item_id, reason in failed.items()]})
    with csv_path.open("w", newline="", encoding="utf-8") as stream:
        fields = ["batchIndex", "libraryRelativePath", "legacyDavItemId", "status", "reason"]
        writer = csv.DictWriter(stream, fieldnames=fields)
        writer.writeheader()
        writer.writerows(rows)
    log(f"reconstructed batch 1 outcomes: exact={len(exact)}, not-imported={len(rows)}")


def main():
    REPORTS.mkdir(parents=True, exist_ok=True)
    JOURNAL_DIR.mkdir(parents=True, exist_ok=True)
    reconstruct_first_batch_report()
    full = request("/api/migration/nzbdav/full/status")
    batches = full.get("batches", [])
    next_index = next((int(b["batchIndex"]) for b in batches if b.get("status") != "acknowledged"), len(batches))
    log(f"resuming Plex migration at batch index {next_index}; {BATCH_COUNT} total")
    for index in range(next_index, BATCH_COUNT):
        stage_name, destination, manifest = stage_batch(index)
        ensure_scan_and_run(index, stage_name, destination, manifest)
        process_batch(index, stage_name, destination, manifest)
        if index + 1 < BATCH_COUNT:
            health = urllib.request.urlopen(HOST + "/health", timeout=10)
            if health.status != 200:
                raise RuntimeError("InfiniDysk health check failed after an acknowledged batch")
    write_final_report()
    full = request("/api/migration/nzbdav/full/status")
    save_json(REPORTS / "full-migration-status-final.json", full)
    log(f"all batches acknowledged: batches={len(full.get('batches', []))}, links={full.get('selectedCount')}, applied={full.get('appliedCount')}, validated={full.get('validatedCount')}")


if __name__ == "__main__":
    lock_path = "/run/lock/infinidysk-plex-import.lock"
    lock_fd = os.open(lock_path, os.O_RDWR | os.O_CREAT, 0o600)
    try:
        try:
            fcntl.flock(lock_fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError as error:
            raise RuntimeError("A Plex migration runner is already active") from error
        main()
    except Exception as error:
        log(f"STOPPED SAFELY: {type(error).__name__}: {error}")
        if any(marker in str(error) for marker in (
            "uncertain external outcome", "reconcile before resuming",
            "existing migration evidence differs", "already active",
            "Expected one Radarr/Sonarr media file",
            "no longer matches the sealed mapped inventory",
            "source symlink changed", "Planned source link",
            "Excluded source is present", "missing source evidence changed",
            "accounted for", "Validation evidence does not match")):
            sys.exit(78)
        raise
    finally:
        os.close(lock_fd)
