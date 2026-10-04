#!/usr/bin/env python3
"""Build an auditable Plex-hub schedule without changing sealed import packages."""
import argparse
import hashlib
import json
import os
import tempfile
import posixpath
import subprocess
import time
import urllib.parse
import urllib.request
import xml.etree.ElementTree as ET
from pathlib import Path


def tier(hub):
    label = (hub.get('hubIdentifier', '') + ' ' + hub.get('title', '')).lower()
    if any(word in label for word in ('continue', 'ondeck', 'on deck', 'next up', 'nextup')):
        return 0
    if any(word in label for word in ('trending', 'recommend')):
        return 1
    return 2


def local_relative(file, mappings, library_root):
    if not file or not file.startswith('/') or '..' in file.split('/'):
        return None
    for mapping in sorted(mappings, key=lambda item: len(item['PlexPath']), reverse=True):
        prefix = mapping['PlexPath'].rstrip('/')
        if file.startswith(prefix + '/') and mapping.get('LocalPath'):
            file = mapping['LocalPath'].rstrip('/') + file[len(prefix):]
            break
    root = library_root.rstrip('/')
    if not file.startswith(root + '/'):
        return None
    return posixpath.normpath(file[len(root) + 1:])


def plex_get(server, key):
    # Hub keys are server-local paths. Never send credentials to a supplied URL.
    parsed = urllib.parse.urlsplit(key)
    if parsed.scheme or parsed.netloc or not key.startswith('/') or key.startswith('//') or parsed.fragment:
        raise ValueError('Plex hub key must be a server-local path')
    query = urllib.parse.parse_qsl(parsed.query, keep_blank_values=True)
    if any(name.lower() == 'x-plex-token' for name, _ in query):
        raise ValueError('Plex hub key contains authentication')
    req = urllib.request.Request(server['Url'].rstrip('/') + key,
        headers={'X-Plex-Token': server['Token'], 'Accept': 'application/xml'})
    with urllib.request.urlopen(req, timeout=30) as response:
        return ET.fromstring(response.read())


def paged(server, key, get=plex_get, limit=None):
    parsed = urllib.parse.urlsplit(key)
    query = [(name, value) for name, value in urllib.parse.parse_qsl(parsed.query, keep_blank_values=True)
             if name not in ('X-Plex-Container-Start', 'X-Plex-Container-Size')]
    offset = 0
    for _ in range(100):
        page_key = parsed.path + '?' + urllib.parse.urlencode(query + [
            ('X-Plex-Container-Start', str(offset)), ('X-Plex-Container-Size', '200')])
        root = get(server, page_key)
        items = [node for node in root if node.tag in ('Video', 'Directory')]
        if not items:
            return
        if limit is not None and offset + len(items) >= limit:
            yield from items[:limit - offset]
            return
        yield from items
        offset += len(items)
        if offset >= int(root.get('totalSize', root.get('size', len(items)))):
            return
    raise ValueError('Plex pagination exceeded its bound; snapshot incomplete')


def collect_files(servers, library_root, get=plex_get):
    files = {}
    hubs = []
    for server in servers:
        if not server.get('Enabled', True):
            continue
        for hub in get(server, '/hubs?includeExternalMetadata=0').findall('Hub'):
            if not hub.get('key') or hub.get('type') not in ('movie', 'episode', 'show', 'season', 'mixed'):
                continue
            priority = tier(hub)
            evidence = {'serverId': server['Id'], 'hubId': hub.get('hubIdentifier'),
                        'title': hub.get('title'), 'tier': priority, 'matchedFileCount': 0}
            seen = set()
            for item in paged(server, hub.get('key'), get, limit=50):
                if item.get('type') in ('show', 'season'):
                    rating = item.get('ratingKey', '')
                    if not rating.isdecimal():
                        raise ValueError('Plex series is missing its rating key')
                    # A series hub ranks its next five unwatched episodes; On Deck supplies
                    # the actual next episode and therefore wins the higher tier.
                    children = paged(server, f'/library/metadata/{rating}/allLeaves?sort=parentIndex:asc,index:asc', get)
                else:
                    children = [item]
                unwatched = 0
                for child in children:
                    if item.get('type') in ('show', 'season') and int(child.get('viewCount', '0')) > 0:
                        continue
                    unwatched += 1
                    for part in child.findall('.//Part'):
                        relative = local_relative(part.get('file'), server.get('PathMappings', []), library_root)
                        if relative is None:
                            continue
                        seen.add(relative)
                        previous = files.get(relative)
                        if previous is None or priority < previous['tier']:
                            files[relative] = {'tier': priority, 'hubId': evidence['hubId'],
                                               'title': child.get('title'), 'serverId': server['Id']}
                    if item.get('type') in ('show', 'season') and unwatched >= 5:
                        break
            evidence['matchedFileCount'] = len(seen)
            hubs.append(evidence)
    return files, hubs


def build_schedule(batches_root, master, batch_count, files, hubs):
    rows = []
    for index in range(batch_count):
        raw = (Path(batches_root) / f'batch-{index + 1:04d}' / 'manifest.json').read_bytes()
        manifest = json.loads(raw)
        if manifest.get('masterManifestDigest') != master or manifest.get('batchIndex') != index \
                or manifest.get('batchCount') != batch_count:
            raise ValueError(f'Batch {index + 1} does not belong to this sealed master')
        matches = {link['libraryRelativePath']: files[link['libraryRelativePath']]
                   for link in manifest['selectedLinks'] if link['libraryRelativePath'] in files}
        counts = [sum(match['tier'] == rank for match in matches.values()) for rank in range(3)]
        rows.append({'batchIndex': index, 'manifestDigest': hashlib.sha256(raw).hexdigest(),
                     'countsByTier': counts, 'matches': matches})
    order = sorted(range(batch_count), key=lambda index: (
        next((rank for rank, count in enumerate(rows[index]['countsByTier']) if count), 3),
        *(-count for count in rows[index]['countsByTier']), index))
    return {'schemaVersion': 1, 'createdAt': time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime()),
            'masterManifestDigest': master, 'batchCount': batch_count,
            'hubItemLimit': 50, 'unwatchedEpisodesPerSeries': 5,
            'order': order, 'batches': rows, 'hubs': hubs}


def load_schedule(path, master, batch_count):
    if not Path(path).exists():
        return None
    plan = json.loads(Path(path).read_text())
    order = plan.get('order', [])
    if plan.get('schemaVersion') != 1 or plan.get('masterManifestDigest') != master \
            or plan.get('batchCount') != batch_count or len(order) != batch_count \
            or any(type(index) is not int for index in order) or sorted(order) != list(range(batch_count)):
        raise ValueError('Plex priority schedule is not a complete permutation of this master')
    rows = plan.get('batches', [])
    if len(rows) != batch_count or sorted(row['batchIndex'] for row in rows) != list(range(batch_count)):
        raise ValueError('Plex priority schedule is missing batch evidence')
    return plan


def next_batch(batches, batch_count, schedule=None):
    indices = [int(batch['batchIndex']) for batch in batches]
    if len(set(indices)) != len(indices) or any(index not in range(batch_count) for index in indices):
        raise ValueError('Registered batch indices are invalid')
    active = [int(batch['batchIndex']) for batch in batches if batch.get('status') != 'acknowledged']
    if len(active) > 1:
        raise ValueError('Multiple unacknowledged batches; reconcile before resuming')
    if active:
        return active[0]
    # Always establish a new recovery master with batch zero.
    if not batches:
        return 0
    if schedule is None and sorted(indices) != list(range(len(indices))):
        raise ValueError('A reordered migration requires its persisted priority schedule')
    order = schedule['order'] if schedule else range(batch_count)
    return next((index for index in order if index not in indices), None)


def verify_selected_manifest(schedule, batches_root, index):
    if schedule is None:
        return
    row = next(row for row in schedule['batches'] if row['batchIndex'] == index)
    raw = (Path(batches_root) / f'batch-{index + 1:04d}' / 'manifest.json').read_bytes()
    if hashlib.sha256(raw).hexdigest() != row['manifestDigest']:
        raise ValueError('Selected batch manifest differs from the Plex priority snapshot')


def configured_servers():
    sql = 'SELECT "ConfigValue" FROM "ConfigItems" WHERE "ConfigName" = $$plex.servers$$ LIMIT 1;\n'
    result = subprocess.run(['docker', 'exec', '-i', 'infinidysk-postgres', 'sh', '-c',
        'psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -At'],
        input=sql, text=True, capture_output=True, check=True)
    return json.loads(result.stdout.strip() or '[]')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--batches', required=True, type=Path)
    parser.add_argument('--master', required=True)
    parser.add_argument('--batch-count', required=True, type=int)
    parser.add_argument('--library-root', default='/mnt/plex')
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    if args.output.exists():
        raise ValueError('Use a new snapshot output; existing scheduling evidence is preserved')
    files, hubs = collect_files(configured_servers(), args.library_root)
    if not files:
        raise ValueError('Plex returned no eligible files; no priority snapshot written')
    plan = build_schedule(args.batches, args.master, args.batch_count, files, hubs)
    # Publish a complete snapshot atomically, refusing to replace existing evidence.
    with tempfile.NamedTemporaryFile(mode='w', dir=args.output.parent, delete=False) as stream:
        temporary = Path(stream.name)
        json.dump(plan, stream, indent=2)
        stream.write('\n')
    try:
        os.link(temporary, args.output)
    finally:
        temporary.unlink()
    print(json.dumps({'hubCount': len(hubs), 'fileCount': len(files),
                      'order': [index + 1 for index in plan['order']], 'output': str(args.output)}))


if __name__ == '__main__':
    main()
