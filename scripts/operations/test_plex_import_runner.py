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


class FrozenValidationCoverageTests(unittest.TestCase):
    def test_same_count_different_paths_and_duplicate_results_are_rejected(self):
        source = Path(__file__).with_name('plex-import-runner.py').read_text()
        function = next(n for n in ast.parse(source).body if isinstance(n, ast.FunctionDef)
                        and n.name == 'require_validation_coverage')
        context = {}
        exec(compile(ast.Module(body=[function], type_ignores=[]), 'coverage', 'exec'), context)
        check = context['require_validation_coverage']
        journal = {'links': [{'libraryRelativePath': 'A', 'status': 'applied'},
                             {'libraryRelativePath': 'B', 'status': 'applied'},
                             {'libraryRelativePath': 'missing', 'status': 'source-missing'}]}
        check(journal, [{'libraryRelativePath': 'B'}, {'libraryRelativePath': 'A'}])
        for paths in (['A', 'C'], ['A', 'A'], ['A'], ['A', 'B', 'missing']):
            with self.assertRaisesRegex(RuntimeError, 'cover'):
                check(journal, [{'libraryRelativePath': path} for path in paths])


class ValidationUnitRecoveryTests(unittest.TestCase):
    def test_existing_validation_is_adopted_only_for_identical_command(self):
        from types import SimpleNamespace
        from unittest.mock import Mock
        source = Path(__file__).with_name('plex-import-runner.py').read_text()
        function = next(n for n in ast.parse(source).body if isinstance(n, ast.FunctionDef)
                        and n.name == 'run_validation_tool')
        with tempfile.TemporaryDirectory() as temp:
            output = Path(temp) / 'validation.json'
            args = ['validate-links', '--journal', 'batch-A', '--output', str(output)]
            commands = Mock()
            commands.run.side_effect = [SimpleNamespace(stdout='active'), SimpleNamespace(stdout='inactive')]
            commands.check_output.return_value = 'tool ' + ' '.join(args)
            run = Mock()
            context = dict(subprocess=commands, TOOL='tool', log=Mock(), Path=Path,
                           time=SimpleNamespace(sleep=lambda _: output.write_text('{}')), run_tool=run)
            exec(compile(ast.Module(body=[function], type_ignores=[]), 'validation-unit', 'exec'), context)
            context['run_validation_tool']('unit-A', args)
            run.assert_not_called()
            commands.run.side_effect = [SimpleNamespace(stdout='active')]
            commands.check_output.return_value = 'tool validate-links --journal batch-B'
            with self.assertRaisesRegex(RuntimeError, 'differs'):
                context['run_validation_tool']('unit-A', args)
            run.assert_not_called()


class TwoBatchPipelineTests(unittest.TestCase):
    def run_pipeline(self, validation_fails=False):
        from concurrent.futures import ThreadPoolExecutor
        from threading import Event
        from types import SimpleNamespace
        from plex_hub_priority import next_pipeline_batch
        source = Path(__file__).with_name('plex-import-runner.py').read_text()
        function = next(n for n in ast.parse(source).body if isinstance(n, ast.FunctionDef) and n.name == 'main')
        module = ast.Module(body=[function], type_ignores=[])
        batches = [{'batchIndex': 0, 'status': 'validating'}, {'batchIndex': 1, 'status': 'running'}]
        validating = Event()
        importing = Event()
        actions = []
        def finish(index, destination, manifest):
            actions.append(('validate', index))
            if index == 0:
                validating.set()
                if not importing.wait(5):
                    raise AssertionError('next import did not overlap validation')
                if validation_fails:
                    raise RuntimeError('validation failed')
            batches[index]['status'] = 'acknowledged'
            actions.append(('ack', index))
        def import_batch(index, *args, **kwargs):
            self.assertEqual(index, 1)
            self.assertTrue(validating.wait(5))
            actions.append(('import', index))
            importing.set()
        def freeze(index, *args):
            self.assertEqual(batches[0]['status'], 'acknowledged')
            batches[index]['status'] = 'validating'
            actions.append(('freeze', index))
        def request(path):
            return {'masterManifestDigest': 'master', 'batches': [dict(b) for b in batches]}
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            context = dict(ThreadPoolExecutor=ThreadPoolExecutor, REPORTS=root, JOURNAL_DIR=root,
                reconstruct_first_batch_report=lambda: None, request=request, MASTER='master',
                BATCH_COUNT=2, PRIORITY_SCHEDULE=root, BATCHES=root, next_pipeline_batch=next_pipeline_batch,
                load_schedule=lambda *args: None, verify_selected_manifest=lambda *args: None,
                stage_batch=lambda i: (str(i), root, {}), finish_batch=finish,
                ensure_scan_and_run=import_batch, process_batch=freeze, log=lambda *args: None,
                urllib=SimpleNamespace(request=SimpleNamespace(urlopen=lambda *args, **kwargs: SimpleNamespace(status=200))),
                HOST='host', write_final_report=lambda: actions.append(('report', None)),
                save_json=lambda *args: None)
            exec(compile(module, 'pipeline-main', 'exec'), context)
            if validation_fails:
                with self.assertRaisesRegex(RuntimeError, 'validation failed'):
                    context['main']()
                self.assertNotIn(('freeze', 1), actions)
                self.assertNotIn(('report', None), actions)
            else:
                context['main']()
                self.assertLess(actions.index(('import', 1)), actions.index(('ack', 0)))
                self.assertLess(actions.index(('ack', 0)), actions.index(('freeze', 1)))
                self.assertEqual([b['status'] for b in batches], ['acknowledged', 'acknowledged'])
                self.assertIn(('report', None), actions)

    def test_restart_recovers_both_slots_and_import_overlaps_validation(self):
        self.run_pipeline()

    def test_validation_failure_preserves_next_batch_without_admitting_more(self):
        self.run_pipeline(validation_fails=True)


if __name__ == '__main__':
    unittest.main()
