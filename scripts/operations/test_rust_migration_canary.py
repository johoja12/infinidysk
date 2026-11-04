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
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary);original=root/'original';original.mkdir()
            (original/'fixture').mkdir()
            mount=root/'mount';mount.mkdir();source=root/'source';source.mkdir()
            pkg=root/'package';expected=package(pkg)
            leaf=json.loads((pkg/'manifest.json').read_text())['releases'][0]['leaves'][0]
            item=leaf['legacyDavItemId'];target=mount/links.ids_path(item).lstrip('/')
            target.parent.mkdir(parents=True);target.write_bytes(b'\x1aE\xdf\xa3')
            original_link=original/'fixture/file.mkv'
            os.symlink('/legacy/.ids/'+item,original_link);before=original_link.lstat()
            nonce=b'0123456789abcdef';probe_path=links.ids_path(str(uuid.uuid4()))
            probe_file=mount/probe_path.lstrip('/');probe_file.parent.mkdir(parents=True,exist_ok=True);probe_file.write_bytes(nonce)
            probe={'ids_path':probe_path,'size':16,'sha256':canary.runner.sha(nonce)}
            class Client:
                url='http://127.0.0.1:1'
                def __init__(self,*args):pass
                def call(self,path,body=None):
                    if path.endswith('/status'):return {'commit':'a'*40,'source_repository':'johoja12/infinidysk'}
                    return probe
                def json(self,path,request):
                    return {**{k:v for k,v in request.items() if k!='source_config'},'version':1,
                            'current_item':item,'ids_path':links.ids_path(item),'source_revision':'b'*64,
                            'record_digest':'c'*64,'payload_validated':False,'observed_at':123}
            key=root/'key';key.write_text('fixture-key');key.chmod(0o600)
            plan=root/'plan.json';dest=root/'canary'
            args=['canary','--package',str(pkg),'--manifest-digest',expected,'--url',Client.url,
                  '--server-commit','a'*40,'--api-key-file',str(key),'--legacy-config',str(source),
                  '--original-library',str(original),'--canary-mount',str(mount),'--canary-library',str(dest),'--plan-file',str(plan)]
            with patch.object(canary.runner,'Client',Client):
                for action in ([],['--apply'],['--apply']):
                    with patch.object(sys,'argv',args+action):canary.main()
            self.assertEqual(os.readlink(dest/'fixture/file.mkv'),str(target))
            self.assertEqual(original_link.lstat().st_ctime_ns,before.st_ctime_ns)
            # No source, package, server or credentials needed for conditional rollback.
            with patch.object(sys,'argv',['canary','--rollback','--canary-library',str(dest),'--plan-file',str(plan)]):canary.main()
            self.assertFalse((dest/'fixture/file.mkv').is_symlink())
            self.assertEqual(original_link.lstat().st_ino,before.st_ino)

    def test_timeout_is_pending_and_proof_changes_are_refused(self):
        with patch.object(canary.subprocess,'run',side_effect=subprocess.TimeoutExpired('fixture',60)):
            with self.assertRaisesRegex(RuntimeError,'pending'):canary.sample_mount('/fixture',{})
        with self.assertRaisesRegex(ValueError,'changed'):
            canary.same_proof({'source_revision':'a','payload_validated':False},{'source_revision':'b','payload_validated':False})

if __name__=='__main__':unittest.main()
