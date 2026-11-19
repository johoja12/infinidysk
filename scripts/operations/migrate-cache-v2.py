#!/usr/bin/env python3
"""Copy one running legacy cache folder to v2; keep sources and a durable cursor."""
import argparse
import fcntl
import json
import os
from pathlib import Path
import sys
import tempfile
import time
import urllib.error
import urllib.request


def save(path, state):
    fd, temporary = tempfile.mkstemp(prefix=path.name + '.', dir=path.parent)
    try:
        with os.fdopen(fd, 'w') as out:
            json.dump(state, out)
            out.write('\n')
            out.flush()
            os.fsync(out.fileno())
        os.replace(temporary, path)
        directory = os.open(path.parent, os.O_RDONLY | os.O_DIRECTORY)
        try:
            os.fsync(directory)
        finally:
            os.close(directory)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


def run(args):
    key = os.environ.get('INFINIDYSK_API_KEY')
    if not key:
        raise ValueError('Set INFINIDYSK_API_KEY in the environment')
    path = Path(args.checkpoint).resolve()
    path.parent.mkdir(parents=True, exist_ok=True)
    lock_fd = os.open(str(path) + '.lock', os.O_CREAT | os.O_RDWR, 0o600)
    with os.fdopen(lock_fd, 'w') as lock:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        identity = {'url': args.url.rstrip('/'), 'source_folder': args.source_folder}
        state = json.loads(path.read_text()) if path.exists() else {
            **identity, 'cursor': {'after_key': '', 'offset': -1},
            'processed_bytes': 0, 'complete': False, 'rejected_entries': 0, 'rejected_blocks': 0,
        }
        if any(state.get(k) != v for k, v in identity.items()):
            raise ValueError('Checkpoint belongs to a different server or source folder')
        if state['rejected_entries'] or state['rejected_blocks']:
            raise ValueError('Checkpoint records rejected data; review it before continuing')
        save(path, state)
        for _ in range(args.max_turns):
            if state['complete']:
                return 0
            payload = {'use_running': True, 'source_folder': args.source_folder, 'cursor': state['cursor']}
            req = urllib.request.Request(identity['url'] + '/api/v1/native-cache/import-legacy',
                data=json.dumps(payload).encode(), headers={
                    'X-Api-Key': key, 'Content-Type': 'application/json'})
            # Do not retry uncertain requests here. Replaying the saved cursor on
            # the next invocation is safe because publication checks v2 itself.
            with urllib.request.urlopen(req, timeout=90) as response:
                report = json.load(response)
            state['cursor'] = report['cursor']
            state['complete'] = report['complete']
            for field in ['processed_bytes', 'rejected_entries', 'rejected_blocks']:
                state[field] += report[field]
            state['last_report'] = report
            save(path, state)
            print(json.dumps({k: state[k] for k in ['processed_bytes', 'complete', 'rejected_entries', 'rejected_blocks']}), flush=True)
            if report['paused'] or report['rejected_entries'] or report['rejected_blocks']:
                print('Paused; inspect the checkpoint and storage status. Sources were retained.', file=sys.stderr)
                return 2
            if not state['complete']:
                time.sleep(args.interval)
        return 0 if state['complete'] else 2


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--url', required=True)
    p.add_argument('--source-folder', required=True)
    p.add_argument('--checkpoint', required=True)
    p.add_argument('--max-turns', type=int, default=100)
    p.add_argument('--interval', type=float, default=1.0)
    args = p.parse_args()
    if args.max_turns < 1 or args.interval < 0:
        p.error('max-turns must be positive and interval nonnegative')
    try:
        return run(args)
    except urllib.error.HTTPError as e:
        print(f'Migration request failed: HTTP {e.code}; checkpoint retained.', file=sys.stderr)
    except (OSError, ValueError, KeyError):
        print('Migration stopped; check connectivity, credentials and checkpoint. No automatic retry.', file=sys.stderr)
    return 1


if __name__ == '__main__':
    sys.exit(main())
