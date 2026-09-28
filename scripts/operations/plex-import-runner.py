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
CLEANUP_ENV = None
CLEANUP_ARR = None


def config_value(container, name):
    if name not in ("api.key", "arr.instances"):
        raise RuntimeError("Unexpected cleanup configuration key")
    sql = f'SELECT "ConfigValue" FROM "ConfigItems" WHERE "ConfigName" = $${name}$$ LIMIT 1;\n'
    result = subprocess.run(
        ["docker", "exec", "-i", container, "sh", "-c",
         'psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -At'],
        input=sql, text=True, capture_output=True, check=True)
    value = result.stdout.strip()
    if not value:
        raise RuntimeError(f"Missing cleanup configuration {name} in {container}")
    return value


def prepare_cleanup_credentials():
    global CLEANUP_ENV, CLEANUP_ARR
    runtime = Path("/run/infinidysk-migration-cleanup")
    runtime.mkdir(mode=0o700, exist_ok=True)
    runtime.chmod(0o700)
    legacy_key = config_value("nzbdav-postgres", "api.key")
    if not all(re.fullmatch(r"[A-Za-z0-9_-]+", key) for key in (KEY, legacy_key)):
        raise RuntimeError("Unexpected API key format for private systemd environment file")
    arr = json.loads(config_value("infinidysk-postgres", "arr.instances"))
    if not any(item.get("Enabled", True) for kind in ("RadarrInstances", "SonarrInstances")
               for item in arr.get(kind, [])):
        raise RuntimeError("No enabled Arr instance configured for cleanup")
    CLEANUP_ENV = runtime / f"credentials-{os.getpid()}.env"
    CLEANUP_ARR = runtime / f"arr-{os.getpid()}.json"
    for path, contents in (
        (CLEANUP_ENV, f"INFINIDYSK_MIGRATION_API_KEY={KEY}\nNZBDAV_MIGRATION_LEGACY_API_KEY={legacy_key}\n"),
        (CLEANUP_ARR, json.dumps(arr) + "\n"),
    ):
        fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
        with os.fdopen(fd, "w", encoding="utf-8") as stream:
            stream.write(contents)


def remove_cleanup_credentials():
    for path in (CLEANUP_ENV, CLEANUP_ARR):
        if path is not None:
            path.unlink(missing_ok=True)


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
    if state["sessionStatus"] == "scanned":
        digest = hashlib.sha256((destination / "SHA256SUMS").read_bytes()).hexdigest()
        request("/api/migration/nzbdav/run", "POST", {
            "packageDigest": digest,
            "selectionCount": len(manifest["selectedLinks"]),
        })
    elif state["sessionStatus"] == "paused":
        request("/api/migration/nzbdav/run/resume", "POST", {})
    return wait_session({"complete"}, batch_index, "import")


def run_tool(service, args, cleanup=False, allow_failure=False):
    command = ["systemd-run", "--wait", "--pipe", "--collect", f"--unit={service}",
               f"--property=EnvironmentFile={ENV_FILE}", "--property=CPUQuota=50%",
               "--property=Nice=19", "--property=IOWeight=10"]
    if cleanup:
        if CLEANUP_ENV is None:
            raise RuntimeError("Private cleanup credentials were not prepared")
        command.append(f"--property=EnvironmentFile={CLEANUP_ENV}")
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
            reason = fail_by_id.get(item_id, {}).get("reason", "terminal import failure")
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


def cleanup_failure_args(batch_index, package, report_dir, historical=False, preflight=False):
    args = ["cleanup-failed-imports", "--failures", str(report_dir / "import-failures.json"),
            "--package", str(package), "--mapped-inventory", str(INVENTORY),
            "--source-root", LIBRARY_ROOT, "--arr-root", LIBRARY_ROOT,
            "--arr-config", str(CLEANUP_ARR), "--infinidysk-url", HOST,
            "--legacy-url", "http://127.0.0.1:8081", "--journal",
            str(report_dir / "failed-cleanup-journal.json")]
    if historical:
        args.extend(("--historical-correlation", str(report_dir / "correlation.json"),
                     "--historical-acknowledgement", str(report_dir / "acknowledgement.json"),
                     "--skip-changed-historical-sources", "true"))
    if preflight:
        args.extend(("--preflight-only", "true"))
    return args


def replay_historical_failures():
    for index in range(6):
        report_dir = (Path("/opt/infinidysk-migration/backups/pr88-plex-restart-20260928/"
                           "batch-0001-reconstructed-evidence") if index == 0
                      else REPORTS / f"batch-{index + 1:04d}")
        package = BATCHES / f"batch-{index + 1:04d}"
        run_tool(f"id64-plex-cleanup-preflight-{index + 1:04d}-20260928",
                 cleanup_failure_args(index, package, report_dir, True, True), cleanup=True)
    for index in range(6):
        report_dir = (Path("/opt/infinidysk-migration/backups/pr88-plex-restart-20260928/"
                           "batch-0001-reconstructed-evidence") if index == 0
                      else REPORTS / f"batch-{index + 1:04d}")
        package = BATCHES / f"batch-{index + 1:04d}"
        run_tool(f"id64-plex-cleanup-history-{index + 1:04d}-20260928",
                 cleanup_failure_args(index, package, report_dir, True), cleanup=True)
        log(f"batch {index + 1} confirmed failure cleanup complete")


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


def cleanup_unreadable_links(batch_index, package, manifest, report_dir, plan_path,
                             journal, validation_path, failures):
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
    report_path = report_dir / "validation-cleanup-failures.json"
    write_json_once(report_path, report)
    base = ["cleanup-validation-failures", "--failures", str(report_path),
            "--package", str(package), "--mapped-inventory", str(INVENTORY),
            "--source-root", LIBRARY_ROOT, "--arr-root", LIBRARY_ROOT,
            "--arr-config", str(CLEANUP_ARR), "--infinidysk-url", HOST,
            "--legacy-url", "http://127.0.0.1:8081", "--journal",
            str(report_dir / "validation-cleanup-journal.json"),
            "--validation-plan", str(plan_path), "--validation-journal", str(journal),
            "--validation-results", str(validation_path)]
    run_tool(f"id64-plex-unreadable-preflight-{batch_index + 1:04d}-20260928",
             base + ["--preflight-only", "true"], cleanup=True)
    run_tool(f"id64-plex-unreadable-cleanup-{batch_index + 1:04d}-20260928", base, cleanup=True)
    write_json_once(report_dir / "validation-failures.json", failures)
    skipped_csv = report_dir / "validation-not-imported.csv"
    if not skipped_csv.exists():
        with skipped_csv.open("x", newline="", encoding="utf-8") as stream:
            writer = csv.DictWriter(stream, fieldnames=["batchIndex", "libraryRelativePath",
                                                       "legacyDavItemId", "status", "reason"])
            writer.writeheader()
            for row in rows:
                writer.writerow({"batchIndex": batch_index,
                                 "libraryRelativePath": row["libraryRelativePath"],
                                 "legacyDavItemId": row["legacyDavItemId"],
                                 "status": "validation-failed", "reason": row["reason"]})
    log(f"batch {batch_index + 1} unreadable links cleaned: {len(rows)}")
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


def process_batch(batch_index, stage_name, destination, manifest):
    request("/api/migration/nzbdav/reconcile", "POST", {}, timeout=3600)
    report_dir, excluded = record_batch_outcomes(
        batch_index,
        request("/api/migration/nzbdav/correlation", timeout=3600),
        request("/api/migration/nzbdav/import-failures", timeout=3600))
    run_tool(f"id64-plex-cleanup-preflight-{batch_index + 1:04d}-20260928",
             cleanup_failure_args(batch_index, destination, report_dir, preflight=True), cleanup=True)
    run_tool(f"id64-plex-cleanup-current-{batch_index + 1:04d}-20260928",
             cleanup_failure_args(batch_index, destination, report_dir), cleanup=True)
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
    if not journal.exists():
        run_tool(f"id64-plex-apply-{batch_index + 1:04d}-20260927", [
            "apply-sharded-links", "--plan", str(plan_path), "--mapped-inventory", str(INVENTORY),
            "--source-root", LIBRARY_ROOT, "--library-root", TARGET_LIBRARY,
            "--target-root", TARGET_ROOT, "--journal", str(journal)])
    journal_data = json.loads(journal.read_text())
    if journal_data.get("planSha256") != digest or journal_data.get("sourceRoot") != LIBRARY_ROOT \
            or journal_data.get("libraryRoot") != TARGET_LIBRARY \
            or journal_data.get("targetRoot") != TARGET_ROOT:
        raise RuntimeError("persisted apply journal does not match this plan and library roots")
    applied = sum(1 for item in journal_data.get("links", []) if item.get("status") == "applied")
    if applied != exact_count:
        raise RuntimeError(f"batch {batch_index + 1} applied {applied} of {exact_count} exact links")
    if not validated_path.exists():
        run_tool(f"id64-plex-validate-{batch_index + 1:04d}-20260927", [
            "validate-links", "--journal", str(journal), "--output", str(validated_path),
            "--max-read-bytes", "65536", "--timeout-seconds", "20"], allow_failure=True)
    if not validated_path.exists():
        raise RuntimeError(f"batch {batch_index + 1} validation produced no durable result")
    validations, resolved_path = retry_failed_validations(batch_index, journal, validated_path)
    validated = sum(1 for item in validations if item.get("success") is True)
    failures_validation = [item for item in validations if item.get("success") is not True]
    if validated + len(failures_validation) != exact_count:
        raise RuntimeError(f"batch {batch_index + 1} validation coverage disagrees with the exact plan")
    validation_rejects = []
    promotion_journal = journal
    if failures_validation:
        validation_rejects = cleanup_unreadable_links(
            batch_index, destination, manifest, report_dir, plan_path, journal,
            resolved_path, failures_validation)
        promotion_journal = filtered_journal(batch_index, journal,
            {row["libraryRelativePath"] for row in failures_validation})
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
    ack = request(f"/api/migration/nzbdav/full/batches/{batch_index}/acknowledge-plan", "POST", {
        "planDigest": digest, "appliedCount": applied, "validatedCount": validated,
        "unvalidatedExactSourceIds": validation_rejects})
    save_json(report_dir / "acknowledgement.json", ack)
    log(f"batch {batch_index + 1}/{BATCH_COUNT} acknowledged: selected={len(manifest['selectedLinks'])}, validated={validated}, unreadable={len(validation_rejects)}, not-imported={len(excluded) + len(validation_rejects)}")
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
    prepare_cleanup_credentials()
    replay_historical_failures()
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
            "existing migration evidence differs", "already active")):
            sys.exit(78)
        raise
    finally:
        remove_cleanup_credentials()
        os.close(lock_fd)
