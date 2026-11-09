#!/usr/bin/env python3
"""Verify sealed 1.x packages and resume Rust migration submissions.

Acceptance is not member correlation. Completed imports remain awaiting validation
until the Rust source-proof endpoint supplies an exact mapping. This runner never
removes library links, calls Arr, or replays an uncertain dispatch with missing proof.
"""
import argparse
import base64
import concurrent.futures
import gzip
import hashlib
import json
import os
from pathlib import Path
import stat
import struct
import threading
import time
import uuid
import urllib.error
import urllib.parse
import urllib.request

MAX_NZB = 256_000_000
MAX_JSON = 128_000_000


def sha(raw):
    return hashlib.sha256(raw).hexdigest()


def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(',', ':'), ensure_ascii=False).encode()


def digest(value):
    return sha(canonical(value))


def lower_digest(value):
    return isinstance(value, str) and len(value) == 64 and all(c in '0123456789abcdef' for c in value)


def unique_json(raw):
    def pairs(items):
        result = {}
        for key, value in items:
            if key in result:
                raise ValueError('Duplicate JSON member')
            result[key] = value
        return result
    return json.loads(raw, object_pairs_hook=pairs)


def path_parts(value):
    if not isinstance(value, str) or not value or value.startswith('/') or '\\' in value \
            or any(ord(c) < 32 for c in value) or (len(value) > 1 and value[1] == ':'):
        raise ValueError('Unsafe package-relative path')
    parts = value.split('/')
    if any(part in ('', '.', '..') for part in parts):
        raise ValueError('Unsafe package-relative path')
    return parts


class Package:
    def __init__(self, root, expected):
        if not lower_digest(expected):
            raise ValueError('An independently retained manifest digest is required')
        self.root = os.open(root, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
        try:
            self.raw = self.read('manifest.json', MAX_JSON)
            if sha(self.raw) != expected:
                raise ValueError('Manifest differs from the retained receipt')
            self.manifest = unique_json(self.raw)
            self.digest = expected
            self.validate()
        except BaseException:
            self.close()
            raise

    def close(self):
        if self.root is not None:
            os.close(self.root)
            self.root = None

    def read(self, name, limit):
        parts = path_parts(name)
        parent = os.dup(self.root)
        try:
            for part in parts[:-1]:
                child = os.open(part, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW, dir_fd=parent)
                os.close(parent)
                parent = child
            fd = os.open(parts[-1], os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK, dir_fd=parent)
            with os.fdopen(fd, 'rb') as stream:
                info = os.fstat(stream.fileno())
                if not stat.S_ISREG(info.st_mode) or info.st_nlink != 1 or info.st_size > limit:
                    raise ValueError('Package entry is not a bounded private regular file')
                raw = stream.read(limit + 1)
                if len(raw) > limit:
                    raise ValueError('Package entry exceeds bound')
                return raw
        finally:
            os.close(parent)

    def validate(self):
        m = self.manifest
        fields = {'schemaVersion', 'packageId', 'createdAt', 'source', 'releases', 'selectedLinks',
                  'payloads', 'checksums', 'masterManifestDigest', 'batchIndex', 'batchCount'}
        if not isinstance(m, dict) or set(m) - fields or m.get('schemaVersion') not in (1, 2) \
                or type(m.get('schemaVersion')) is not int or m.get('source') != 'nzbdav' or not isinstance(m.get('packageId'), str) \
                or not m['packageId'] or len(m['packageId']) > 512:
            raise ValueError('Unsupported mapped manifest')
        if m['schemaVersion'] == 2 and (not lower_digest(m.get('masterManifestDigest'))
                or type(m.get('batchIndex')) is not int or type(m.get('batchCount')) is not int
                or not 0 <= m['batchIndex'] < m['batchCount']):
            raise ValueError('Invalid sealed batch binding')
        releases = m.get('releases', [])
        links = m.get('selectedLinks', [])
        if not 1 <= len(releases) <= 10_000 or len(links) > 250_000:
            raise ValueError('Mapped package count exceeds bound')
        payloads = self.unique_entries(m['payloads'], 'relativePath')
        sums = self.unique_entries(m['checksums'], 'relativePath')
        if set(sums) != set(payloads):
            raise ValueError('Payload and declared checksum sets differ')
        leaf_ids, ready, excluded, release_ids, payload_paths = {}, [], [], set(), set()
        for release in releases:
            if set(release) - {'sourceReleaseId', 'nzbBlobId', 'payloadPath', 'leaves', 'sourceFileName', 'sourceJobName'}:
                raise ValueError('Unknown release field')
            key = release['sourceReleaseId']
            if not isinstance(key, str) or not key or len(key) > 512 or key in release_ids:
                raise ValueError('Duplicate or invalid source release identity')
            release_ids.add(key)
            name = release['payloadPath']
            path_parts(name)
            if name not in payloads or name in payload_paths:
                raise ValueError('Release does not bind one unique payload')
            payload_paths.add(name)
            entry = payloads[name]
            raw = self.read(name, MAX_NZB)
            if type(entry['length']) is not int or entry['length'] != len(raw) \
                    or not lower_digest(entry['sha256']) or sha(raw) != entry['sha256'] \
                    or sums[name]['sha256'] != entry['sha256']:
                raise ValueError('Payload length/digest differs')
            if (release.get('sourceFileName') is None) != (release.get('sourceJobName') is None):
                raise ValueError('Source names must be provided together')
            for leaf in release['leaves']:
                if set(leaf) - {'legacyDavItemId','legacyPath','fileSize','parentReleaseId','historyId','nzbBlobId',
                               'identityKind','identityDigest','extractionStatus','exclusionReason',
                               'stableIdentityKind','stableIdentityDigest'}:
                    raise ValueError('Unknown source leaf field')
                key = leaf['legacyDavItemId']
                parsed_id = uuid.UUID(key)
                if parsed_id.int == 0 or str(parsed_id) != key:
                    raise ValueError('Source leaf needs a nonempty canonical UUID')
                if key in leaf_ids or leaf['parentReleaseId'] != release['sourceReleaseId'] \
                        or type(leaf['fileSize']) is not int or leaf['fileSize'] < 0:
                    raise ValueError('Invalid or duplicate source leaf binding')
                leaf_ids[key] = leaf
                if leaf['extractionStatus'] == 'ready':
                    if not lower_digest(leaf.get('identityDigest')):
                        raise ValueError('Ready source identity is missing')
                    if m['schemaVersion'] == 2 and leaf['identityKind'] == 'archive-articles-v2' \
                            and (leaf.get('stableIdentityKind') != leaf['identityKind']
                                 or leaf.get('stableIdentityDigest') != leaf['identityDigest']):
                        raise ValueError('Stable archive identity differs')
                    ready.append(leaf)
                elif leaf['extractionStatus'] == 'excluded':
                    excluded.append(leaf)
                else:
                    raise ValueError('Unknown source extraction outcome')
        if payload_paths != set(payloads):
            raise ValueError('Unbound package payload')
        link_paths, selected = set(), set()
        for link in links:
            if set(link) != {'libraryRelativePath','originalTarget','legacyDavItemId'}:
                raise ValueError('Invalid selected link shape')
            path_parts(link['libraryRelativePath'])
            leaf = leaf_ids.get(link['legacyDavItemId'])
            if link['libraryRelativePath'] in link_paths or link['legacyDavItemId'] in selected \
                    or leaf is None or leaf['extractionStatus'] != 'ready':
                raise ValueError('Selected link is ambiguous or lacks a ready leaf')
            link_paths.add(link['libraryRelativePath'])
            selected.add(link['legacyDavItemId'])
        for name, expected in [('success.json', ready), ('exclusions.json', excluded)]:
            if unique_json(self.read(name, MAX_JSON)) != expected:
                raise ValueError('Export report differs from sealed leaf outcomes')
        expected_files = set(payloads) | {'manifest.json','success.json','exclusions.json'}
        lines = self.read('SHA256SUMS', 4_000_000).decode().splitlines()
        checked = set()
        for line in lines:
            checksum, sep, name = line.partition('  ')
            if not sep or not lower_digest(checksum) or name in checked or name not in expected_files:
                raise ValueError('Invalid package checksum set')
            checked.add(name)
            if sha(self.read(name, MAX_NZB if name in payloads else MAX_JSON)) != checksum:
                raise ValueError('Package checksum differs')
        if checked != expected_files:
            raise ValueError('Incomplete package checksum set')

    def payload(self, release):
        raw = self.read(release['payloadPath'], MAX_NZB)
        expected = next(entry for entry in self.manifest['payloads'] if entry['relativePath'] == release['payloadPath'])
        if len(raw) != expected['length'] or sha(raw) != expected['sha256']:
            raise ValueError('Payload changed after package verification')
        return raw

    @staticmethod
    def unique_entries(items, key):
        result = {}
        for item in items:
            name = item[key]
            expected_fields = {'relativePath','sha256','length'} if 'length' in item else {'relativePath','sha256'}
            if set(item) != expected_fields:
                raise ValueError('Invalid payload/checksum entry shape')
            path_parts(name)
            if name in result:
                raise ValueError('Duplicate package entry')
            result[name] = item
        return result


def hash_parts(parts):
    value = hashlib.sha256()
    for part in parts:
        raw = part.encode()
        value.update(struct.pack('>Q', len(raw)))
        value.update(raw)
    return value.hexdigest()


def intent(package, release):
    return hash_parts(['migration-intent-v1', package, release])


def xml_digest(raw):
    if raw.startswith(b'\x1f\x8b'):
        import io
        with gzip.GzipFile(fileobj=io.BytesIO(raw)) as stream:
            raw = stream.read(MAX_NZB + 1)
    if len(raw) > MAX_NZB:
        raise ValueError('Decompressed NZB exceeds bound')
    return sha(raw)


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        raise ValueError('Migration API redirect refused')


class HttpFailure(RuntimeError):
    def __init__(self,status):
        self.status=status
        super().__init__('Migration request returned HTTP '+str(status)+'; no replay is authorized')


class Client:
    def __init__(self, url, key):
        if not key or len(key)>4096 or any(ord(c)<33 or ord(c)>126 for c in key):
            raise ValueError('Invalid private API key')
        if any(ord(c)<32 or ord(c)==127 for c in url):
            raise ValueError('Invalid API URL')
        parsed = urllib.parse.urlsplit(url)
        if parsed.scheme not in ('http','https') or not parsed.netloc or parsed.username \
                or parsed.password or parsed.query or parsed.fragment:
            raise ValueError('API URL must be an explicit origin/base path without credentials')
        self.url, self.key = url.rstrip('/'), key
        self.opener = urllib.request.build_opener(NoRedirect)

    def call(self, path, body=None, options=None, content_type='application/x-nzb'):
        headers = {'X-Api-Key':self.key, 'Content-Type':content_type, 'Accept':'application/json'}
        if options is not None:
            headers['X-Migration-Options'] = base64.b64encode(canonical(options)).decode('ascii')
        request = urllib.request.Request(self.url + path, data=body, headers=headers)
        try:
            with self.opener.open(request, timeout=60) as response:
                raw = response.read(MAX_JSON + 1)
                if len(raw) > MAX_JSON:
                    raise ValueError('Migration response exceeds bound')
                return unique_json(raw)
        except urllib.error.HTTPError as error:
            raise HttpFailure(error.code) from None
        except (urllib.error.URLError, TimeoutError, OSError) as error:
            # HTTP exception strings can include private URLs and response data.
            raise RuntimeError('Migration request failed; read back its intent before retry') from None


    def json(self,path,value):
        return self.call(path,canonical(value),content_type='application/json')


class Journal:
    def __init__(self, path, scope):
        self.lock = threading.Lock()
        self.fd = os.open(path, os.O_RDWR | os.O_APPEND | os.O_CREAT | os.O_NOFOLLOW | os.O_NONBLOCK, 0o600)
        try:
            self._load(scope)
        except BaseException:
            os.close(self.fd)
            raise

    def _load(self, scope):
        info = os.fstat(self.fd)
        if not stat.S_ISREG(info.st_mode) or info.st_nlink != 1 or info.st_mode & 0o077:
            raise ValueError('Runner journal must be an owner-only regular file')
        import fcntl
        fcntl.flock(self.fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
        self.scope = scope
        self.latest = {}
        with os.fdopen(os.dup(self.fd), 'rb') as stream:
            raw = stream.read(64_000_001)
        if len(raw) > 64_000_000:
            raise ValueError('Runner journal exceeds bound')
        if raw and not raw.endswith(b'\n'):
            raise ValueError('Runner journal has an incomplete final line; preserve and recover it explicitly')
        for line in raw.splitlines():
            row = unique_json(line)
            if row['scope'] != scope:
                raise ValueError('Runner journal belongs to a different plan/server')
            self.latest[row['intent']] = row

    def append(self, key, state, receipt=None):
        row = {'version':1,'scope':self.scope,'intent':key,'state':state,'at':time.time_ns() // 1_000_000}
        if receipt is not None:
            row['receipt'] = receipt
        raw = canonical(row) + b'\n'
        with self.lock:
            view = memoryview(raw)
            while view:
                written = os.write(self.fd, view)
                if not written:
                    raise OSError('Journal write made no progress')
                view = view[written:]
            os.fsync(self.fd)
            self.latest[key] = row

    def close(self):
        os.close(self.fd)


def submit_one(package, release, client, journal, category, priority):
    key = intent(package.digest, release['sourceReleaseId'])
    raw = package.payload(release)
    filename = release.get('sourceFileName') or release['sourceReleaseId'] + '.nzb'
    job_name = release.get('sourceJobName') or release['sourceReleaseId']
    scope = hash_parts(['migration-options-v1',filename,job_name,category,str(priority),'0'])
    payload = xml_digest(raw)
    receipt = client.call('/api/v1/migration/submissions/' + key)
    previous = journal.latest.get(key)
    if receipt is None:
        if previous is not None:
            raise ValueError('A previous dispatch has no acceptance receipt; held for explicit recovery')
        journal.append(key, 'dispatching')
        options = {'package_digest':package.digest,'source_release_id':release['sourceReleaseId'],
            'file_name':filename,'job_name':job_name,'category':category,'priority':priority,'post_processing':0}
        # Any exception leaves dispatching durable. The next pass observes first.
        receipt = client.call('/api/v1/migration/submissions', raw, options=options)
    if receipt['intent'] != key or receipt['scope'] != scope or receipt['payload_sha256'] != payload:
        raise ValueError('Server acceptance does not bind this exact source/options')
    if previous and previous.get('receipt', {}).get('job_id',receipt['job_id']) != receipt['job_id']:
        raise ValueError('Accepted job identity changed; held for recovery')
    journal.append(key, receipt['state'], receipt)
    return receipt


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--package', required=True)
    parser.add_argument('--manifest-digest', required=True)
    parser.add_argument('--url')
    parser.add_argument('--server-commit')
    parser.add_argument('--api-key-file')
    parser.add_argument('--journal')
    parser.add_argument('--category', default='migration-canary')
    parser.add_argument('--import-slots',type=int,default=2)
    parser.add_argument('--priority',type=int,default=0)
    parser.add_argument('--run', action='store_true', help='Dispatch missing intents; default verifies the package only')
    args = parser.parse_args()
    if not 1 <= args.import_slots <= 8 or not -32768 <= args.priority <= 32767:
        parser.error('Invalid slots or priority')
    if args.run and (not args.url or not args.api_key_file or not args.journal or not args.server_commit
                     or len(args.server_commit)!=40 or any(c not in '0123456789abcdef' for c in args.server_commit)):
        parser.error('Dispatch requires an explicit URL, exact source commit, private API-key file and journal')
    package = Package(args.package, args.manifest_digest)
    try:
        if not args.run:
            print(json.dumps({'verified':True,'manifestDigest':package.digest,'releases':len(package.manifest['releases']),
                              'selectedLinks':len(package.manifest['selectedLinks']),'dispatched':False}))
            return
        fd = os.open(args.api_key_file, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
        with os.fdopen(fd) as stream:
            info = os.fstat(stream.fileno())
            if not stat.S_ISREG(info.st_mode) or info.st_mode & 0o077 or info.st_nlink != 1:
                raise ValueError('API key file must be owner-only and regular')
            key = stream.read(4097).strip()
            if not key or len(key)>4096:
                raise ValueError('Invalid private API key file')
        client = Client(args.url,key)
        status = client.call('/api/v1/status')
        if status.get('commit') != args.server_commit or status.get('source_repository') != 'johoja12/infinidysk':
            raise ValueError('API source identity differs from the trial plan')
        scope = digest({'version':1,'package':package.digest,'url':client.url,'commit':args.server_commit,
                        'category':args.category,'priority':args.priority})
        journal = Journal(args.journal,scope)
        try:
            releases = package.manifest['releases']
            active = [r for r in releases if journal.latest.get(intent(package.digest,r['sourceReleaseId']),{}).get('state') not in ('completed','failed','accepted_history_unavailable','completed_source_missing')]
            # Completed/failed acceptance remains durable. Never turn history pruning
            # or an uncertain dispatch into a new NZB. Validation is a separate lane.
            with concurrent.futures.ThreadPoolExecutor(max_workers=args.import_slots) as pool:
                results = list(pool.map(lambda release:submit_one(package,release,client,journal,args.category,args.priority), active[:args.import_slots]))
            counts={}
            for release in releases:
                state=journal.latest.get(intent(package.digest,release['sourceReleaseId']),{}).get('state','pending')
                counts[state]=counts.get(state,0)+1
            completed=bool(releases) and set(counts)=={'completed'}
            terminal=bool(releases) and set(counts)<=set(('completed','failed','accepted_history_unavailable','completed_source_missing'))
            print(json.dumps({'manifestDigest':package.digest,'observed':len(results),'states':[r['state'] for r in results],
                              'releaseCount':len(releases),'releaseStates':counts,'allCompleted':completed,'allTerminal':terminal,
                              'acknowledged':False,'next':'Verify exact imported member identities before any link promotion'}))
        finally:
            journal.close()
    finally:
        package.close()


if __name__ == '__main__':
    try:
        main()
    except (ValueError,RuntimeError,OSError,KeyError,TypeError) as error:
        raise SystemExit(str(error)) from None
