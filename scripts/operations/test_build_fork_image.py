"""No Docker/network: exercise exact-source and namespace admission in private repos."""
import json
import pathlib
import shutil
import subprocess
import sys
import tempfile
import unittest


class BuildPlan(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='id-fork-build-test-')
        self.addCleanup(self.temp.cleanup)
        self.root = pathlib.Path(self.temp.name)
        script = self.root / 'scripts/operations/build-fork-image.py'
        script.parent.mkdir(parents=True)
        shutil.copyfile(pathlib.Path(__file__).with_name('build-fork-image.py'), script)
        self.script = script
        for command in [
            ['git', 'init', '--quiet', '-b', 'fixture'],
            ['git', 'config', 'user.name', 'Fixture'],
            ['git', 'config', 'user.email', 'fixture@example.invalid'],
            ['git', 'add', '.'], ['git', 'commit', '--quiet', '-m', 'chore(test): seed fixture'],
        ]:
            subprocess.run(command, cwd=self.root, check=True)

    def invoke(self, *args):
        return subprocess.run([sys.executable, str(self.script), '--plan', *args],
                              cwd=self.root, text=True, capture_output=True, check=False)

    def test_plan_records_exact_commit_without_docker(self):
        result = self.invoke()
        self.assertEqual(result.returncode, 0, result.stderr)
        receipt = json.loads(result.stdout)
        commit = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=self.root, text=True).strip()
        self.assertEqual(receipt['source_commit'], commit)
        self.assertEqual(receipt['image'], f'ghcr.io/johoja12/infinidysk-rust-trial:sha-{commit[:12]}')

    def test_dirty_or_untracked_source_is_refused(self):
        (self.root / 'uncommitted').write_text('fixture')
        self.assertNotEqual(self.invoke().returncode, 0)

    def test_upstream_source_identity_is_refused(self):
        self.assertNotEqual(self.invoke('--repository', 'infinidysk/infinidysk').returncode, 0)

    def test_upstream_registry_and_mutable_tags_are_refused(self):
        for image in ['ghcr.io/infinidysk/infinidysk-next:edge',
                      'ghcr.io/johoja12/infinidysk-rust-trial:latest']:
            self.assertNotEqual(self.invoke('--image', image).returncode, 0)


if __name__ == '__main__':
    unittest.main()
