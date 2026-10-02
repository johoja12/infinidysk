import json
import sqlite3
import tempfile
import unittest
from pathlib import Path

from existing_id_imports import id_target, skip_existing, REASON


class ExistingIdImportsTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        self.library = self.root / 'library'
        self.library.mkdir()
        self.target = self.root / 'id'
        self.db = self.root / 'migration.db'
        self.report = self.root / 'reports' / 'already.json'
        self.manifest = {'batchIndex': 27, 'selectedLinks': [
            {'legacyDavItemId': 'one', 'libraryRelativePath': 'one'},
            {'legacyDavItemId': 'two', 'libraryRelativePath': 'two'}], 'releases': [
                {'sourceReleaseId': 'a', 'leaves': [{'legacyDavItemId': 'one'}]},
                {'sourceReleaseId': 'b', 'leaves': [{'legacyDavItemId': 'two'}]}]}
        with sqlite3.connect(self.db) as db:
            db.executescript('''
                CREATE TABLE SessionState(Id,Status,SourceType,SourcePackageRoot);
                INSERT INTO SessionState VALUES(1,'paused','nzbdav','/config/package');
                CREATE TABLE Submissions(StoreRef,State,NzoId,Attempt);
                INSERT INTO Submissions VALUES('nzbdav:a','pending',NULL,0),('nzbdav:b','pending',NULL,0);
                CREATE TABLE Releases(StoreRef,Included,Verdict,VerdictReasons);
                INSERT INTO Releases VALUES('nzbdav:a',1,'green','[]'),('nzbdav:b',1,'green','[]');
                CREATE TABLE MigratedReleases(SourceType,SourceReleaseId);
                CREATE TABLE ReleaseFiles(StoreRef,NewDavItemId);
            ''')

    def apply(self):
        return skip_existing(self.db, self.manifest, self.library, self.target, '/config/package', self.report)

    def query(self, sql):
        with sqlite3.connect(self.db) as db:
            return db.execute(sql).fetchall()

    def test_skip_dangling_id_link_preserve_legacy_and_repeat(self):
        (self.library / 'one').symlink_to(self.target / '.ids' / 'one')
        (self.library / 'two').symlink_to(self.root / 'legacy' / 'two')
        self.assertEqual(len(self.apply()), 1)
        self.assertEqual(self.query('SELECT StoreRef FROM Submissions'), [('nzbdav:b',)])
        self.assertIn(REASON, self.query("SELECT VerdictReasons FROM Releases WHERE StoreRef='nzbdav:a'")[0][0])
        self.assertTrue((self.library / 'one').is_symlink())
        self.assertEqual(len(self.apply()), 1)
        self.assertEqual(len(list(self.report.parent.glob('*.db'))), 2)

    def test_changed_skip_stops_resume(self):
        path = self.library / 'one'
        path.symlink_to(self.target / 'one')
        self.apply()
        path.unlink()
        path.symlink_to(self.root / 'legacy' / 'one')
        with self.assertRaisesRegex(RuntimeError, 'reconcile before resuming'):
            self.apply()

    def test_mixed_release_keeps_submission(self):
        self.manifest['releases'][0]['leaves'].append({'legacyDavItemId': 'two'})
        self.manifest['releases'].pop()
        (self.library / 'one').symlink_to(self.target / 'one')
        (self.library / 'two').symlink_to(self.root / 'legacy' / 'two')
        self.assertEqual(self.apply(), [])
        self.assertEqual(len(self.query('SELECT * FROM Submissions')), 2)

    def test_regular_missing_and_prefix_collision_not_skipped(self):
        (self.library / 'one').write_text('local file')
        (self.library / 'two').symlink_to(str(self.target) + '-other/file')
        self.assertEqual(self.apply(), [])

    def test_relative_target(self):
        (self.library / 'one').symlink_to('../id/file')
        self.assertEqual(id_target(self.library, 'one', self.target), '../id/file')

    def test_submission_retry_or_inflight_preserved(self):
        (self.library / 'one').symlink_to(self.target / 'one')
        with sqlite3.connect(self.db) as db:
            db.execute("UPDATE Submissions SET Attempt=1 WHERE StoreRef='nzbdav:a'")
        self.assertEqual(self.apply(), [])
        self.assertEqual(len(self.query('SELECT * FROM Submissions')), 2)

    def test_running_session_refused_without_changes(self):
        (self.library / 'one').symlink_to(self.target / 'one')
        with sqlite3.connect(self.db) as db:
            db.execute("UPDATE SessionState SET Status='running'")
        with self.assertRaisesRegex(RuntimeError, 'scanned or paused'):
            self.apply()
        self.assertEqual(len(self.query('SELECT * FROM Submissions')), 2)

    def test_symlink_parent_and_traversal_refused(self):
        (self.library / 'alias').symlink_to(self.library, target_is_directory=True)
        with self.assertRaisesRegex(RuntimeError, 'real directory'):
            id_target(self.library, 'alias/file', self.target)
        for path in ('../file', '/file', 'a//file'):
            with self.assertRaisesRegex(RuntimeError, 'Unsafe'):
                id_target(self.library, path, self.target)


if __name__ == '__main__':
    unittest.main()
