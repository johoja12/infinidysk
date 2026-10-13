"""Offline behavior checks: no live hosts, credentials or media required."""
import argparse
import hashlib
import importlib.util
import json
import sqlite3
import sys
import tempfile
import threading
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from unittest.mock import patch
from urllib.error import HTTPError

sys.dont_write_bytecode = True

spec = importlib.util.spec_from_file_location('collector', Path(__file__).with_name('collect.py'))
collector = importlib.util.module_from_spec(spec)
spec.loader.exec_module(collector)


class EvidenceTests(unittest.TestCase):
    def test_preview_uses_current_parts_and_does_not_write_database(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            source = root / '.ids' / '12345678-1234-4234-9234-123456789abc'
            source.parent.mkdir()
            source.write_bytes(b'fixture')
            current = root / 'Episode Proper.mkv'
            current.symlink_to(source)
            db = root / 'preview.sqlite'
            with sqlite3.connect(db) as connection:
                connection.execute('CREATE TABLE mediaitem (id INTEGER, status TEXT, file_path TEXT, media_parts_info TEXT)')
                connection.execute('INSERT INTO mediaitem VALUES (1,?,?,?)', (
                    'FAILED', str(root / 'obsolete.mkv'), json.dumps([{'file_path': str(current), 'bundle_hash': 'newhash'}]),
                ))
                connection.execute('INSERT INTO mediaitem VALUES (2,?,?,?)', ('PROCESSING', str(current), None))
            before = hashlib.sha256(db.read_bytes()).digest()
            result = collector.collect_preview(db)
            self.assertEqual(before, hashlib.sha256(db.read_bytes()).digest())
            failed = next(row for row in result['items'] if row['id'] == 1)
            self.assertFalse(failed['cachedFile']['existsOnCollectorHost'])
            self.assertEqual(failed['parts'][0]['idItemId'], source.name)
            self.assertTrue(failed['parts'][0]['existsOnCollectorHost'])
            self.assertNotIn('last_attempted_at', result['availableColumns'])
            self.assertEqual(result['statusCounts'], {'FAILED': 1, 'PROCESSING': 1})

    def test_api_snapshot_retains_pair_identity_and_reports_pagination_gaps(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            config = root / 'arr.json'
            config.write_text(json.dumps({'SonarrInstances': [
                {'Name': 'Sonarr', 'Host': 'http://fixture', 'ApiKey': 'private-arr-key'},
                {'Name': 'Disabled', 'Host': 'http://disabled', 'ApiKey': 'unused', 'Enabled': False},
            ]}))
            key = root / 'id-key'
            key.write_text('private-id-key')
            args = argparse.Namespace(postgres_container=None, arr_config_file=config,
                                      id_key_file=key, id_url='http://id', reconcile_library=True)
            calls = []

            def read(url, secret):
                calls.append(url)
                if '/queue?' in url:
                    return {'totalRecords': 3, 'records': [
                        {'id': 1, 'downloadId': 'paired', 'episodeId': 11, 'status': 'completed',
                         'statusMessages': [{'messages': ['private-arr-key https://provider/file.nzb&r=hidden']}],
                         'downloadUrl': 'https://provider/?apikey=hidden'},
                        {'id': 2, 'downloadId': 'paired', 'episodeId': 12, 'status': 'completed'},
                    ]}
                if '/episode/' in url:
                    return {'episodeFileId': 8, 'episodeNumber': 1, 'sceneEpisodeNumber': 3}
                if '/episodefile/' in url:
                    return {'path': str(root / 'unmounted.mkv'), 'sceneName': 'paired'}
                if '/health' in url:
                    return []
                if '/system/status' in url:
                    return {'version': 'fixture'}
                return {'status': True}

            with patch.object(collector, 'get_json', side_effect=read):
                result = collector.collect_arr(args)
            instance = result['instances'][0]
            self.assertEqual(len(result['instances']), 1)
            self.assertTrue(instance['paginationIncomplete'])
            self.assertEqual([row['episodeId'] for row in instance['rows']], [11, 12])
            self.assertTrue(all(row['library']['hasFile'] for row in instance['rows']))
            self.assertFalse(instance['rows'][0]['library']['existsOnCollectorHost'])
            serialized = json.dumps(result)
            self.assertNotIn('private-arr-key', serialized)
            self.assertNotIn('hidden', serialized)
            self.assertNotIn('downloadUrl', serialized)
            self.assertFalse(any('/disabled' in url for url in calls))

    def test_redirect_does_not_forward_api_credentials(self):
        received = []

        class Destination(BaseHTTPRequestHandler):
            def do_GET(self):
                received.append(self.headers.get('x-api-key'))
                self.send_response(200)
                self.end_headers()
                self.wfile.write(b'{}')

            def log_message(self, *args):
                pass

        destination = ThreadingHTTPServer(('127.0.0.1', 0), Destination)

        class Redirect(BaseHTTPRequestHandler):
            def do_GET(self):
                self.send_response(302)
                self.send_header('Location', f'http://127.0.0.1:{destination.server_port}/file.nzb&r=private')
                self.end_headers()

            def log_message(self, *args):
                pass

        redirect = ThreadingHTTPServer(('127.0.0.1', 0), Redirect)
        servers = [destination, redirect]
        for server in servers:
            threading.Thread(target=server.serve_forever, daemon=True).start()
        try:
            with self.assertRaises(HTTPError) as context:
                collector.get_json(f'http://127.0.0.1:{redirect.server_port}/', 'private-api-key')
            self.assertEqual(collector.safe_error(context.exception), {'type': 'HTTPError', 'httpStatus': 302})
            self.assertEqual(received, [])
        finally:
            for server in servers:
                server.shutdown()
                server.server_close()


if __name__ == '__main__':
    unittest.main()
