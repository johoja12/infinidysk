"""Checkpoint recovery and source isolation for the operator migration command."""
import argparse
import contextlib
import importlib.util
import io
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('migrate', Path(__file__).with_name('migrate-cache-v2.py'))
migrate = importlib.util.module_from_spec(spec)
spec.loader.exec_module(migrate)


class CheckpointTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.args = argparse.Namespace(url='http://127.0.0.1:18080', source_folder='pilot',
            checkpoint=str(Path(self.directory.name) / 'progress.json'), max_turns=1, interval=0)
        self.key = patch.dict(os.environ, {'INFINIDYSK_API_KEY': 'fixture-api-key'})
        self.key.start()
        self.addCleanup(self.key.stop)

    def run_with(self, report):
        response = io.BytesIO(json.dumps(report).encode())
        with patch.object(migrate.urllib.request, 'urlopen', return_value=response) as request:
            with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
                result = migrate.run(self.args)
            return result, request

    def test_resume_uses_durable_cursor_and_rejects_source_change(self):
        cursor = {'after_key': 'a' * 64, 'offset': 4194304}
        report = dict(cursor=cursor, processed_bytes=4194304, rejected_entries=0,
            rejected_blocks=0, complete=False, paused=False)
        result, _ = self.run_with(report)
        self.assertEqual(result, 2)
        state = json.loads(Path(self.args.checkpoint).read_text())
        self.assertEqual(state['cursor'], cursor)
        self.assertEqual(os.stat(self.args.checkpoint).st_mode & 0o777, 0o600)
        result, request = self.run_with(dict(report, complete=True, processed_bytes=0))
        self.assertEqual(result, 0)
        payload = json.loads(request.call_args.args[0].data)
        self.assertEqual(payload['cursor'], cursor)
        self.assertEqual(payload['source_folder'], 'pilot')
        self.args.source_folder = 'different-folder'
        with self.assertRaises(ValueError):
            migrate.run(self.args)

    def test_uncertain_request_keeps_previous_cursor_for_safe_replay(self):
        with patch.object(migrate.urllib.request, 'urlopen', side_effect=TimeoutError):
            with self.assertRaises(TimeoutError):
                migrate.run(self.args)
        state = json.loads(Path(self.args.checkpoint).read_text())
        self.assertEqual(state['cursor'], {'after_key': '', 'offset': -1})
        self.assertEqual(state['processed_bytes'], 0)

    def test_rejections_are_saved_and_block_unreviewed_resume(self):
        report = dict(cursor={'after_key': 'b' * 64, 'offset': -1}, processed_bytes=0,
            rejected_entries=1, rejected_blocks=0, complete=True, paused=False)
        result, _ = self.run_with(report)
        self.assertEqual(result, 2)
        with self.assertRaises(ValueError):
            migrate.run(self.args)


if __name__ == '__main__':
    unittest.main()
