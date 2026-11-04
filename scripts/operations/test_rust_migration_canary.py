"""Coordinator tests use generated local files and simulated HTTP proofs only."""
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch
import uuid
import rust_migration_links as links
from test_rust_import_runner import package

SPEC=importlib.util.spec_from_file_location('canary',Path(__file__).with_name('rust-migration-canary.py'))
canary=importlib.util.module_from_spec(SPEC);SPEC.loader.exec_module(canary)

class CanaryTests(unittest.TestCase):
    def test_plan_apply_resume_and_offline_rollback_leave_original_untouched(self):
        self.exercise_workflow(False)

    def test_new_ids_require_exact_completed_job_mapping_and_resume_the_same_canary(self):
        self.exercise_workflow(True)

    def exercise_workflow(self,reimport):
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary);original=root/'original';original.mkdir()
            (original/'fixture').mkdir()
            mount=root/'mount';mount.mkdir();source=root/'source';source.mkdir()
            pkg=root/'package';expected=package(pkg)
            leaf=json.loads((pkg/'manifest.json').read_text())['releases'][0]['leaves'][0]
            old=leaf['legacyDavItemId'];item=str(uuid.uuid4()) if reimport else old
            target=mount/links.ids_path(item).lstrip('/')
            target.parent.mkdir(parents=True);target.write_bytes(b'\x1aE\xdf\xa3')
            original_link=original/'fixture/file.mkv'
            os.symlink('/legacy/.ids/'+old,original_link);before=original_link.lstat()
            nonce=b'0123456789abcdef';probe_path=links.ids_path(str(uuid.uuid4()))
            probe_file=mount/probe_path.lstrip('/');probe_file.parent.mkdir(parents=True,exist_ok=True);probe_file.write_bytes(nonce)
            probe={'ids_path':probe_path,'size':16,'sha256':canary.runner.sha(nonce)}
            self_outer=self
            class Client:
                url='http://127.0.0.1:1'
                def __init__(self,*args):pass
                def call(self,path,body=None):
                    if path.endswith('/status'):return {'commit':'a'*40,'source_repository':'johoja12/infinidysk'}
                    if '/submissions/' in path:
                        return {'state':'completed','intent':canary.runner.intent(expected,'fixture-release'),
                                'payload_sha256':canary.runner.xml_digest((pkg/'payloads/fixture-release.nzb').read_bytes()),
                                'job_id':'11111111-1111-4111-8111-111111111111','download_dir':'22222222-2222-4222-8222-222222222222'}
                    return probe
                def json(self,path,request):
                    extra={}
                    if path.endswith('/reimport-proof'):
                        self_outer.assertEqual(request['current_item'],item)
                        extra={'submission_intent':request['submission_intent'],'job_id':'11111111-1111-4111-8111-111111111111','download_dir':'22222222-2222-4222-8222-222222222222'}
                        request=request['source']
                    return {**{k:v for k,v in request.items() if k!='source_config'},'version':1,
                            'current_item':item,'ids_path':links.ids_path(item),'source_revision':'b'*64,
                            'record_digest':'c'*64,'payload_validated':False,'observed_at':123,**extra}
            key=root/'key';key.write_text('fixture-key');key.chmod(0o600)
            plan=root/'plan.json';dest=root/'canary'
            args=['canary','--package',str(pkg),'--manifest-digest',expected,'--url',Client.url,
                  '--server-commit','a'*40,'--api-key-file',str(key),'--legacy-config',str(source),
                  '--original-library',str(original),'--canary-mount',str(mount),'--canary-library',str(dest),'--plan-file',str(plan)]
            if reimport:
                mapping={'version':1,'packageDigest':expected,'serverCommit':'a'*40,
                         'bindings':[{'legacyItem':old,'currentItem':item,'submissionIntent':canary.runner.intent(expected,'fixture-release'),'sourceReleaseId':'fixture-release'}]}
                raw=canary.runner.canonical(mapping);saved=root/'mapping.json';saved.write_bytes(raw);saved.chmod(0o600)
                checked=canary.runner.Package(pkg,expected)
                try:
                    with self.assertRaisesRegex(ValueError,'retained digest'):
                        canary.load_mapping(saved,'0'*64,checked,'a'*40)
                    with self.assertRaisesRegex(ValueError,'different package or Rust source'):
                        canary.load_mapping(saved,canary.runner.sha(raw),checked,'b'*40)
                finally:
                    checked.close()
                args+=['--reimport-map',str(saved),'--reimport-map-digest',canary.runner.sha(raw)]
            with patch.object(canary.runner,'Client',Client):
                for action in ([],['--apply'],['--apply']):
                    with patch.object(sys,'argv',args+action):canary.main()
            self.assertEqual(os.readlink(dest/'fixture/file.mkv'),str(target))
            self.assertEqual(original_link.lstat().st_ctime_ns,before.st_ctime_ns)
            # No source, package, server or credentials needed for conditional rollback.
            with patch.object(sys,'argv',['canary','--rollback','--canary-library',str(dest),'--plan-file',str(plan)]):canary.main()
            self.assertFalse((dest/'fixture/file.mkv').is_symlink())
            self.assertEqual(original_link.lstat().st_ino,before.st_ino)

    def test_unaccepted_or_nonterminal_job_never_becomes_member_proof(self):
        test=self
        class Client:
            def __init__(self,value):self.value=value
            def call(self,path):return self.value
            def json(self,*args):test.fail('Unaccepted job must not request member correlation')
        entry={'submissionIntent':'a'*64,'currentItem':str(uuid.uuid4()),'sourceReleaseId':'fixture'}
        for receipt in (None,{'state':'queued'},{'state':'failed'},{'state':'accepted_history_unavailable'},
                        {'state':'completed','intent':'a'*64,'payload_sha256':'wrong'}):
            with self.subTest(receipt=receipt),self.assertRaisesRegex(ValueError,'remains pending'):
                canary.fetch_proof(Client(receipt),{'payload_sha256':'b'*64},entry)

    def test_timeout_is_pending_and_proof_changes_are_refused(self):
        with patch.object(canary.subprocess,'run',side_effect=subprocess.TimeoutExpired('fixture',60)):
            with self.assertRaisesRegex(RuntimeError,'pending'):canary.sample_mount('/fixture',{})
        with self.assertRaisesRegex(ValueError,'changed'):
            canary.same_proof({'source_revision':'a','payload_validated':False},{'source_revision':'b','payload_validated':False})

if __name__=='__main__':unittest.main()
