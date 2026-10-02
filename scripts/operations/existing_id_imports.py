"""Exclude pending NZBs whose selected library links already point to InfiniDysk.

The sealed package stays unchanged. Exclusions use the existing red-release /
scan-exclusion ledger path, so correlation and acknowledgement retain coverage.
"""
import errno
import json
import os
import sqlite3
import stat
import time
from pathlib import Path

REASON = 'already-linked-to-infinidysk'


def id_target(library_root, relative, target_root):
    parts = relative.split('/')
    if not relative or any(p in ('', '.', '..') for p in parts):
        raise RuntimeError(f'Unsafe library path: {relative!r}')
    cursor = Path(library_root)
    if not stat.S_ISDIR(cursor.lstat().st_mode):
        raise RuntimeError(f"Library root is not a real directory: {cursor}")
    for part in parts[:-1]:
        cursor /= part
        try:
            if not stat.S_ISDIR(cursor.lstat().st_mode):
                raise RuntimeError(f'Library parent is not a real directory: {cursor}')
        except FileNotFoundError:
            return None
    path = cursor / parts[-1]
    try:
        target = os.readlink(path)
    except FileNotFoundError:
        return None
    except OSError as error:
        if error.errno != errno.EINVAL:
            raise
        return None
    absolute = os.path.normpath(target if os.path.isabs(target) else path.parent / target)
    root = os.path.normpath(target_root)
    # Inspect link text only: a missing/unmounted target must still be preserved.
    return target if absolute.startswith(root + '/') else None


def skip_existing(database, manifest, library_root, target_root, package_root, report_path):
    report_path = Path(report_path)
    if report_path.exists():
        previous = json.loads(report_path.read_text())
        if previous['batchIndex'] != manifest['batchIndex']:
            raise RuntimeError('Existing-ID report belongs to another batch')
        for link in previous['skippedLinks']:
            relative = link['libraryRelativePath']
            if id_target(library_root, relative, target_root) != previous['observedTargets'][relative]:
                raise RuntimeError('Previously skipped InfiniDysk library link changed; reconcile before resuming')
    selected = {str(x['legacyDavItemId']): x for x in manifest['selectedLinks']}
    candidates = []
    for release in manifest['releases']:
        links = [selected[str(x['legacyDavItemId'])] for x in release['leaves']
                 if str(x['legacyDavItemId']) in selected]
        observed = {x['libraryRelativePath']: id_target(
            library_root, x['libraryRelativePath'], target_root) for x in links}
        if links and all(observed.values()):
            candidates.append((release, links, observed))
    if not candidates:
        return []
    database = Path(database)
    if not database.is_file():
        raise RuntimeError('Migration ledger does not exist')
    report_path = Path(report_path)
    report_path.parent.mkdir(parents=True, exist_ok=True)
    with sqlite3.connect(database, timeout=30) as db:
        # Online SQLite backup includes WAL contents; never copy the live db file.
        backup = report_path.with_name(f'migration-before-existing-id-{time.time_ns()}.db')
        with sqlite3.connect(backup) as snapshot:
            db.backup(snapshot)
        backup.chmod(0o600)
        db.execute('BEGIN IMMEDIATE')
        session = db.execute('SELECT Status, SourceType, SourcePackageRoot FROM SessionState WHERE Id=1').fetchone()
        if session is None or session[0] not in ('scanned', 'paused') or session[1] != 'nzbdav' or session[2] != package_root:
            raise RuntimeError('Existing-ID exclusion requires the matching scanned or paused NzbDav session')
        skipped = []
        for release, links, observed in candidates:
            ref = 'nzbdav:' + release['sourceReleaseId']
            row = db.execute('SELECT State, NzoId, Attempt FROM Submissions WHERE StoreRef=?', (ref,)).fetchone()
            verdict = db.execute('SELECT VerdictReasons FROM Releases WHERE StoreRef=?', (ref,)).fetchone()
            if row is None:
                if verdict and REASON in json.loads(verdict[0]):
                    skipped.extend(links)
                continue
            # Never discard an external submission or a retry with uncertain history.
            if row[0] != 'pending' or row[1] is not None or row[2] != 0:
                continue
            if db.execute('SELECT 1 FROM MigratedReleases WHERE SourceType=? AND SourceReleaseId=?', ('nzbdav', ref)).fetchone():
                raise RuntimeError(f'Pending release has conflicting import provenance: {ref}')
            if db.execute('SELECT 1 FROM ReleaseFiles WHERE StoreRef=? AND NewDavItemId IS NOT NULL', (ref,)).fetchone():
                raise RuntimeError(f'Pending release has an existing target mapping: {ref}')
            if verdict is None:
                raise RuntimeError(f'Pending release lacks scan evidence: {ref}')
            if any(id_target(library_root, path, target_root) != target for path, target in observed.items()):
                raise RuntimeError('InfiniDysk library link changed during exclusion preflight')
            reasons = json.loads(verdict[0])
            if REASON not in reasons:
                reasons.append(REASON)
            db.execute('UPDATE Releases SET Included=0, Verdict=?, VerdictReasons=? WHERE StoreRef=?',
                       ('red', json.dumps(reasons), ref))
            db.execute('DELETE FROM Submissions WHERE StoreRef=?', (ref,))
            skipped.extend(links)
        evidence = {'batchIndex': manifest['batchIndex'], 'reason': REASON,
                    'skippedLinks': skipped, 'observedTargets': {p:t for _,_,o in candidates for p,t in o.items()}}
        temporary = report_path.with_suffix('.json.tmp')
        temporary.write_text(json.dumps(evidence, indent=2) + '\n')
        temporary.replace(report_path)
        db.commit()
    return skipped
