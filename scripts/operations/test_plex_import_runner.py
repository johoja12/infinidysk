import ast
import json
import os
import tempfile
import unittest
from pathlib import Path


class DurableReplacementResumeTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        root = Path(self.temp.name)
        self.source = root / 'source'
        self.library = root / 'library'
        self.target = root / 'target'
        self.journals = root / 'journals'
        self.report = root / 'report'
        for path in (self.source, self.library, self.target, self.journals, self.report):
            path.mkdir()
        self.replacement = str(root / 'different-legacy-item')
        (self.source / 'replacement.mkv').symlink_to(self.replacement)
        self.plan = {'links': [
            {'libraryRelativePath': 'applied.mkv', 'correlationStatus': 'exact', 'applyStatus': 'planned'},
            {'libraryRelativePath': 'replacement.mkv', 'correlationStatus': 'exact', 'applyStatus': 'planned',
             'originalLegacyTarget': 'old-legacy-item', 'newRelativeTarget': '.ids/original-import',
             'expectedFileSize': 4},
        ]}
        self.journal = {
            'planSha256': 'sealed-digest', 'sourceRoot': str(self.source),
            'libraryRoot': str(self.library), 'targetRoot': str(self.target),
            'links': [{'libraryRelativePath': 'applied.mkv', 'status': 'applied'}, {
                'libraryRelativePath': 'replacement.mkv', 'status': 'source-replaced',
                'sourceLinkPath': str(self.source / 'replacement.mkv'),
                'linkPath': str(self.library / 'replacement.mkv'),
                'observedSourceTarget': 'old-legacy-item',
                'replacementSourceTarget': self.replacement,
                'targetPath': str(self.target / '.ids/original-import'), 'expectedFileSize': 4,
            }],
        }

    def check_resume(self):
        (self.journals / 'batch-0001.json').write_text(json.dumps(self.journal))
        source = Path(__file__).with_name('plex-import-runner.py').read_text()
        function = next(n for n in ast.parse(source).body if isinstance(n, ast.FunctionDef) and n.name == 'process_batch')
        lines = source.splitlines()
        start = next(n.lineno for n in function.body if isinstance(n, ast.Assign) and any(
            isinstance(t, ast.Name) and t.id == 'journal' for t in n.targets))
        end = next(n.lineno for n in function.body if isinstance(n, ast.Assign) and any(
            isinstance(t, ast.Name) and t.id == 'journal_data' for t in n.targets))
        code = '\n'.join(line[4:] for line in lines[start - 1:end - 1])
        calls = []
        context = dict(json=json, os=os, JOURNAL_DIR=self.journals, batch_index=0,
                       plan=self.plan, report_dir=self.report, digest='sealed-digest',
                       LIBRARY_ROOT=str(self.source), TARGET_LIBRARY=str(self.library),
                       TARGET_ROOT=str(self.target), id_target=lambda *args: None,
                       exact_count=2, plan_path='sealed-plan', INVENTORY='inventory',
                       write_json_once=lambda path, value: None,
                       run_tool=lambda *args: calls.append(args))
        exec(compile(code, 'resume-accounting', 'exec'), context)
        return calls, context

    def test_durable_legacy_replacement_is_accounted_for_without_reapply(self):
        calls, context = self.check_resume()
        self.assertEqual(calls, [])
        self.assertEqual(context['replaced_paths'], {'replacement.mkv': self.replacement})

    def test_changed_replacement_is_rejected(self):
        (self.source / 'replacement.mkv').unlink()
        (self.source / 'replacement.mkv').symlink_to('other-item')
        with self.assertRaisesRegex(RuntimeError, 'reconcile before resuming'):
            self.check_resume()

    def test_journal_for_a_different_plan_is_rejected(self):
        self.journal['planSha256'] = 'different-digest'
        with self.assertRaisesRegex(RuntimeError, 'sealed plan'):
            self.check_resume()


if __name__ == '__main__':
    unittest.main()
