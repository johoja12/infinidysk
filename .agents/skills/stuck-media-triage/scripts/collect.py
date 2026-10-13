#!/usr/bin/env python3
"""Collect private, read-only Arr/InfiniDysk or Plex Preview evidence."""
import argparse
import datetime
import json
import os
import re
import sqlite3
import subprocess
import uuid
from pathlib import Path
from urllib.error import HTTPError
from urllib.parse import urlsplit
from urllib.request import HTTPRedirectHandler, Request, build_opener

QUEUE_FIELDS = (
    'id', 'title', 'downloadId', 'downloadClient', 'status', 'trackedDownloadState',
    'trackedDownloadStatus', 'added', 'estimatedCompletionTime', 'timeleft',
    'sizeleft', 'size', 'episodeId', 'movieId', 'outputPath', 'statusMessages',
)
PREVIEW_FIELDS = (
    'id', 'title', 'library_name', 'status', 'file_path', 'media_parts_info',
    'error_message', 'updated_at', 'last_attempted_at', 'duration', 'progress',
    'current_fps', 'avg_speed', 'processing_worker_type', 'is_priority',
    'priority_score', 'bundle_hash', 'bif_path',
)


class NoRedirect(HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def redact(value, secrets=()):
    if isinstance(value, dict):
        return {key: '[redacted]' if key.lower() in {
            'apikey', 'api_key', 'password', 'token', 'plex_token', 'cookie',
            'authorization', 'secret', 'downloadurl',
        } else redact(item, secrets) for key, item in value.items()}
    if isinstance(value, list):
        return [redact(item, secrets) for item in value]
    if isinstance(value, str):
        for secret in sorted((x for x in secrets if x), key=len, reverse=True):
            value = value.replace(secret, '[redacted]')
        value = re.sub(r'https?://[^\s<>"\']+', '[URL redacted]', value, flags=re.I)
        value = re.sub(r'(?i)\b(api[-_]?key|password|token|authorization)\s*[=:]\s*[^\s,;]+',
                       r'\1=[redacted]', value)
    return value


def get_json(url, key):
    parsed = urlsplit(url)
    if parsed.scheme not in ('http', 'https') or parsed.username or parsed.password:
        raise ValueError('Expected an HTTP(S) endpoint without URL credentials')
    request = Request(url, headers={'x-api-key': key, 'Accept': 'application/json'})
    with build_opener(NoRedirect).open(request, timeout=30) as response:
        return json.load(response)


def pg_config(container):
    # Static SELECT; credentials remain within the configured database process.
    query = '''SELECT "ConfigName", "ConfigValue" FROM "ConfigItems"
               WHERE "ConfigName" IN ('arr.instances', 'api.key');'''
    result = subprocess.run([
        'docker', 'exec', '-i', container, 'sh', '-c',
        'psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -At -F "\t"',
    ], input=query, text=True, capture_output=True, check=True)
    return dict(line.split('\t', 1) for line in result.stdout.splitlines() if '\t' in line)


def file_identity(path):
    result = {'path': path}
    if not path:
        return result
    try:
        result['existsOnCollectorHost'] = Path(path).exists()
        if Path(path).is_symlink():
            target = os.readlink(path)
            result['linkTarget'] = target
            if '/.ids/' in target:
                try:
                    result['idItemId'] = str(uuid.UUID(Path(target).name))
                except ValueError:
                    pass
        if result['existsOnCollectorHost']:
            result['sizeOnCollectorHost'] = Path(path).stat().st_size
    except OSError as error:
        result['statError'] = type(error).__name__
    return result


def library_identity(base, key, row):
    movie = row.get('movieId') is not None
    media_id = row.get('movieId') if movie else row.get('episodeId')
    if media_id is None:
        return None
    item = get_json(base + ('movie/' if movie else 'episode/') + str(media_id), key)
    file_id = item.get('movieFileId' if movie else 'episodeFileId', 0)
    result = {'mediaId': media_id, 'hasFile': bool(file_id), 'fileId': file_id}
    result['episode'] = {field: item.get(field) for field in (
        'title', 'seasonNumber', 'episodeNumber', 'sceneSeasonNumber', 'sceneEpisodeNumber',
    )}
    if file_id:
        media_file = get_json(base + ('moviefile/' if movie else 'episodefile/') + str(file_id), key)
        result.update(file_identity(media_file.get('path')))
        result['sceneName'] = media_file.get('sceneName')
        result['runtime'] = media_file.get('mediaInfo', {}).get('runTime')
    return result


def safe_error(error):
    # Do not serialize exception text, HTTP bodies, headers or redirect locations.
    return {'type': type(error).__name__, 'httpStatus': error.code if isinstance(error, HTTPError) else None}


def collect_arr(args):
    if args.postgres_container:
        config = pg_config(args.postgres_container)
        arr = json.loads(config['arr.instances'])
        id_key = config['api.key']
    else:
        arr = json.loads(args.arr_config_file.read_text())
        id_key = args.id_key_file.read_text().strip()
    instances = [item for group in ('RadarrInstances', 'SonarrInstances')
                 for item in arr.get(group, []) if item.get('Enabled', True)]
    secrets = [id_key] + [item.get('ApiKey', '') for item in instances]
    result = {'instances': [], 'id': {}}
    for instance in instances:
        entry = {'name': instance['Name'], 'rows': [], 'errors': []}
        base = instance['Host'].rstrip('/') + '/api/v3/'
        try:
            data = get_json(base + 'queue?pageSize=1000&includeMovie=true&includeEpisode=true&includeSeries=true', instance['ApiKey'])
            entry['totalRecords'] = data['totalRecords']
            for raw in data['records']:
                row = {field: raw.get(field) for field in QUEUE_FIELDS}
                if args.reconcile_library and row['status'] in ('completed', 'failed'):
                    try:
                        row['library'] = library_identity(base, instance['ApiKey'], row)
                    except Exception as error:
                        row['libraryError'] = safe_error(error)
                entry['rows'].append(row)
            entry['paginationIncomplete'] = data['totalRecords'] > len(entry['rows'])
        except Exception as error:
            entry['errors'].append({'endpoint': 'queue', **safe_error(error)})
        for label, endpoint in [('health', 'health'), ('version', 'system/status')]:
            try:
                data = get_json(base + endpoint, instance['ApiKey'])
                entry[label] = data if label == 'health' else {'version': data.get('version')}
            except Exception as error:
                entry['errors'].append({'endpoint': label, **safe_error(error)})
        result['instances'].append(entry)
    for label, endpoint in (
        ('queue', '/api?mode=queue&output=json'),
        ('history', '/api?mode=history&output=json&pageSize=100'),
        ('prefetch', '/api/prefetch?includeCoverage=false'),
        ('healthQueue', '/api/get-health-check-queue?pageSize=100'),
    ):
        try:
            result['id'][label] = get_json(args.id_url.rstrip('/') + endpoint, id_key)
        except Exception as error:
            result['id'][label] = {'error': safe_error(error)}
    return redact(result, secrets)


def collect_preview(db):
    with sqlite3.connect(db.resolve().as_uri() + '?mode=ro', uri=True, timeout=30) as connection:
        connection.row_factory = sqlite3.Row
        columns = [row['name'] for row in connection.execute('PRAGMA table_info(mediaitem)')]
        fields = [field for field in PREVIEW_FIELDS if field in columns]
        if 'status' not in fields:
            raise ValueError('Preview mediaitem status column unavailable')
        counts = dict(connection.execute('SELECT status, count(*) FROM mediaitem GROUP BY status').fetchall())
        query = 'SELECT ' + ','.join('"' + field + '"' for field in fields)
        query += ' FROM mediaitem WHERE upper(status) IN (?,?,?)'
        rows = [dict(row) for row in connection.execute(query, ('FAILED', 'SLOW_FAILED', 'PROCESSING'))]
    for row in rows:
        row['cachedFile'] = file_identity(row.get('file_path'))
        try:
            parts = json.loads(row.get('media_parts_info') or '[]')
            row['parts'] = [{**file_identity(part.get('file_path') or part.get('file') or part.get('path')),
                            'bundleHash': part.get('bundle_hash'), 'bifPath': part.get('bif_path')}
                           for part in parts]
        except (ValueError, TypeError, AttributeError):
            row['partsError'] = 'Unable to parse media_parts_info'
        row.pop('media_parts_info', None)
    return redact({'availableColumns': columns, 'statusCounts': counts, 'items': rows})


def main():
    os.umask(0o077)
    parser = argparse.ArgumentParser(description=__doc__)
    modes = parser.add_subparsers(dest='mode', required=True)
    arr = modes.add_parser('arr-id')
    auth = arr.add_mutually_exclusive_group(required=True)
    auth.add_argument('--postgres-container')
    auth.add_argument('--arr-config-file', type=Path)
    arr.add_argument('--id-key-file', type=Path)
    arr.add_argument('--id-url', required=True)
    arr.add_argument('--reconcile-library', action='store_true')
    preview = modes.add_parser('preview')
    preview.add_argument('--db', required=True, type=Path)
    for mode in (arr, preview):
        mode.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    if args.mode == 'arr-id' and args.arr_config_file and not args.id_key_file:
        parser.error('--arr-config-file requires --id-key-file')
    args.output.mkdir(mode=0o700, parents=True, exist_ok=True)
    if args.output.is_symlink() or args.output.stat().st_mode & 0o077:
        parser.error('Use a private output directory (mode 0700)')
    destination = args.output / (args.mode + '.json')
    if destination.exists():
        parser.error('Snapshot exists; use a new capture directory')
    try:
        data = collect_arr(args) if args.mode == 'arr-id' else collect_preview(args.db)
    except Exception as error:
        print(json.dumps({'collectionError': safe_error(error)}))
        return 1
    data['capturedAtUtc'] = datetime.datetime.now(datetime.timezone.utc).isoformat()
    with destination.open('x') as output:
        json.dump(data, output, indent=2)
    print(json.dumps({'snapshot': str(destination), 'mode': args.mode}))
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
