"""Generated packages and loopback substitutes only; never uses production."""
import importlib.util
import json
import os
from pathlib import Path
import tempfile
import unittest
import uuid

SPEC = importlib.util.spec_from_file_location('rust_import_runner',Path(__file__).with_name('rust-import-runner.py'))
runner = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(runner)


def package(root, mutate=None):
    leaf={'legacyDavItemId':str(uuid.uuid4()),'legacyPath':'/content/fixture/file.mkv','fileSize':4,
          'parentReleaseId':'fixture-release','historyId':None,'nzbBlobId':None,
          'identityKind':'direct-articles-v1','identityDigest':'a'*64,'extractionStatus':'ready',
          'exclusionReason':None,'stableIdentityKind':None,'stableIdentityDigest':None}
    xml=b'<nzb><file subject="fixture.mkv"><segments><segment bytes="4" number="1">fixture@loopback</segment></segments></file></nzb>'
    payload={'relativePath':'payloads/fixture-release.nzb','length':len(xml),'sha256':runner.sha(xml)}
    m={'schemaVersion':2,'packageId':'fixture-package','createdAt':'2026-11-04T00:00:00Z','source':'nzbdav',
       'masterManifestDigest':'b'*64,'batchIndex':0,'batchCount':1,
       'releases':[{'sourceReleaseId':'fixture-release','nzbBlobId':None,'payloadPath':payload['relativePath'],
                    'leaves':[leaf],'sourceFileName':'fixture.nzb','sourceJobName':'fixture'}],
       'selectedLinks':[{'libraryRelativePath':'fixture/file.mkv','originalTarget':'/legacy/.ids/'+leaf['legacyDavItemId'],
                         'legacyDavItemId':leaf['legacyDavItemId']}],
       'payloads':[payload],'checksums':[{'relativePath':payload['relativePath'],'sha256':payload['sha256']}]}
    if mutate:
        mutate(m)
    root.mkdir()
    (root/'payloads').mkdir()
    files={'manifest.json':runner.canonical(m),'success.json':runner.canonical([leaf]),'exclusions.json':b'[]',payload['relativePath']:xml}
    for name,raw in files.items():
        (root/name).write_bytes(raw)
    (root/'SHA256SUMS').write_text(''.join(runner.sha(raw)+'  '+name+'\n' for name,raw in sorted(files.items())))
    return runner.sha(files['manifest.json'])


class FakeClient:
    def __init__(self, package, fail_after=False):
        self.receipt=None
        self.posts=0
        self.package=package
        self.fail_after=fail_after

    def call(self,path,body=None,options=None):
        if body is None:
            return self.receipt
        self.posts+=1
        key=runner.intent(self.package.digest,'fixture-release')
        self.receipt={'intent':key,'scope':runner.hash_parts(['migration-options-v1','fixture.nzb','fixture','canary','0','0']),
                      'payload_sha256':runner.sha(body),'job_id':'fixture-job','state':'queued'}
        if self.fail_after:
            raise RuntimeError('lost fixture response')
        return self.receipt


class RunnerTests(unittest.TestCase):
    def test_sealed_package_and_rechecked_payload(self):
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary)/'package'
            expected=package(root)
            p=runner.Package(root,expected)
            try:
                self.assertEqual(p.digest,expected)
                self.assertTrue(p.payload(p.manifest['releases'][0]))
                (root/'payloads/fixture-release.nzb').write_bytes(b'changed')
                with self.assertRaisesRegex(ValueError,'changed'):
                    p.payload(p.manifest['releases'][0])
            finally:
                p.close()

    def test_wrong_digest_duplicates_escaping_paths_and_ambiguous_links_are_refused(self):
        changes=[lambda m:m['releases'].append(m['releases'][0]),
                 lambda m:m['selectedLinks'].append(m['selectedLinks'][0]),
                 lambda m:m['selectedLinks'][0].update(libraryRelativePath='../outside.mkv'),
                 lambda m:m['selectedLinks'][0].update(legacyDavItemId=str(uuid.uuid4())),
                 lambda m:m.update(batchIndex=1),
                 lambda m:m['releases'][0]['leaves'][0].update(identityDigest='invalid')]
        for change in changes:
            with self.subTest(change=change), tempfile.TemporaryDirectory() as temporary:
                root=Path(temporary)/'package'
                expected=package(root,change)
                with self.assertRaises(ValueError):
                    runner.Package(root,expected)
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary)/'package'
            package(root)
            with self.assertRaises(ValueError):
                runner.Package(root,'c'*64)

    def test_symlinked_payload_parent_and_incomplete_checksums_are_refused(self):
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary)/'package'
            expected=package(root)
            os.rename(root/'payloads',root/'moved')
            os.symlink(root/'moved',root/'payloads')
            with self.assertRaises(OSError):
                runner.Package(root,expected)
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary)/'package'
            expected=package(root)
            (root/'SHA256SUMS').write_text('')
            with self.assertRaises(ValueError):
                runner.Package(root,expected)

    def test_lost_response_is_read_back_after_journal_restart_without_replay(self):
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary)/'package'
            p=runner.Package(root,package(root))
            journal=runner.Journal(Path(temporary)/'journal','fixture-plan')
            client=FakeClient(p,True)
            release=p.manifest['releases'][0]
            try:
                with self.assertRaises(RuntimeError):
                    runner.submit_one(p,release,client,journal,'canary',0)
                journal.close()
                journal=runner.Journal(Path(temporary)/'journal','fixture-plan')
                receipt=runner.submit_one(p,release,client,journal,'canary',0)
                self.assertEqual(receipt['state'],'queued')
                self.assertEqual(client.posts,1)
                client.receipt=None
                with self.assertRaisesRegex(ValueError,'explicit recovery'):
                    runner.submit_one(p,release,client,journal,'canary',0)
                self.assertEqual(client.posts,1)
            finally:
                journal.close()
                p.close()

    def test_torn_journal_and_changed_plan_are_refused(self):
        with tempfile.TemporaryDirectory() as temporary:
            path=Path(temporary)/'journal'
            journal=runner.Journal(path,'first-plan')
            journal.append('fixture','dispatching')
            journal.close()
            with self.assertRaisesRegex(ValueError,'different plan'):
                runner.Journal(path,'second-plan')
            with path.open('ab') as stream:
                stream.write(b'{')
            with self.assertRaisesRegex(ValueError,'incomplete'):
                runner.Journal(path,'first-plan')


if __name__=='__main__':
    unittest.main()
