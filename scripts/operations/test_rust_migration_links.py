"""Owned temporary files only; filesystem fixtures are not native mount proof."""
import importlib.util
import json
import os
from pathlib import Path
import tempfile
import unittest
import uuid

import rust_migration_links as links
SPEC=importlib.util.spec_from_file_location('runner',Path(__file__).with_name('rust-import-runner.py'))
runner=importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(runner)

class LinksTests(unittest.TestCase):
    def setup_plan(self,root):
        original=root/'original';original.mkdir()
        mount=root/'mount';mount.mkdir()
        item=str(uuid.uuid4());path=links.ids_path(item)
        data=b'\x1aE\xdf\xa3'+bytes(range(256))*256
        target=mount/path.lstrip('/');target.parent.mkdir(parents=True);target.write_bytes(data)
        (original/'folder').mkdir();os.symlink('/legacy/fixture',original/'folder/fixture.mkv')
        proof={'current_item':item,'legacy_item':item,'ids_path':path,'size':len(data),'source_revision':'a'*64,'record_digest':'b'*64}
        source=links.root_fd(mount)
        try:
            sampled=links.sample(source,proof,'.mkv')
        finally:
            os.close(source)
        binding={'link':{'libraryRelativePath':'folder/fixture.mkv','originalTarget':'/legacy/fixture','legacyDavItemId':item},'proof':proof,'sampledValidation':sampled}
        plan=links.plan(original,mount,root/'canary',[binding],'fixture-scope')
        return plan,binding

    def test_samples_and_canary_links_resume_without_changing_originals(self):
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary);plan,binding=self.setup_plan(root)
            original=Path(plan['originalRoot'])/'folder/fixture.mkv';before=original.lstat()
            journal=runner.Journal(root/'journal','fixture-plan')
            try:
                self.assertEqual(links.apply_canary(plan,journal,lambda proof:None),1)
                self.assertEqual(links.apply_canary(plan,journal,lambda proof:None),0)
                self.assertEqual(os.readlink(root/'canary/folder/fixture.mkv'),str(Path(plan['mountRoot']))+binding['proof']['ids_path'])
                after=original.lstat();self.assertEqual((after.st_ino,after.st_ctime_ns,os.readlink(original)),(before.st_ino,before.st_ctime_ns,'/legacy/fixture'))
                self.assertEqual(links.rollback_canary(plan,journal),1)
                self.assertFalse((root/'canary/folder/fixture.mkv').is_symlink())
            finally:
                journal.close()

    def test_changed_original_and_changed_destination_are_never_overwritten(self):
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary);plan,_=self.setup_plan(root);journal=runner.Journal(root/'journal','fixture-plan')
            try:
                original=Path(plan['originalRoot'])/'folder/fixture.mkv';original.unlink();os.symlink('/changed',original)
                with self.assertRaisesRegex(ValueError,'changed'):
                    links.apply_canary(plan,journal,lambda proof:None)
                self.assertEqual(os.readlink(original),'/changed')
            finally:
                journal.close()
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary);plan,_=self.setup_plan(root);journal=runner.Journal(root/'journal','fixture-plan')
            try:
                links.apply_canary(plan,journal,lambda proof:None)
                target=root/'canary/folder/fixture.mkv';target.unlink();os.symlink('/foreign',target)
                self.assertEqual(links.rollback_canary(plan,journal),0)
                self.assertEqual(os.readlink(target),'/foreign')
            finally:
                journal.close()

    def test_same_target_foreign_inode_cannot_be_adopted_on_resume(self):
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary);plan,_=self.setup_plan(root);journal=runner.Journal(root/'journal','fixture-plan')
            try:
                links.apply_canary(plan,journal,lambda proof:None)
                target=root/'canary/folder/fixture.mkv';saved=os.readlink(target)
                target.rename(target.with_name('foreign-backup'))
                os.symlink(saved,target)
                with self.assertRaisesRegex(ValueError,'identity changed'):
                    links.apply_canary(plan,journal,lambda proof:None)
                self.assertEqual(links.rollback_canary(plan,journal),0)
                self.assertTrue(target.is_symlink())
            finally:
                journal.close()

    def test_invalid_probe_or_payload_and_root_overlap_are_refused(self):
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary);plan,binding=self.setup_plan(root)
            fd=links.root_fd(plan['mountRoot'])
            try:
                with self.assertRaises(ValueError):
                    links.check_probe(fd,{'ids_path':binding['proof']['ids_path'],'size':16,'sha256':'a'*64})
                path=Path(plan['mountRoot'])/binding['proof']['ids_path'].lstrip('/')
                path.write_bytes(b'bad!'+bytes(65536))
                with self.assertRaises(ValueError):
                    links.sample(fd,binding['proof'],'.mkv')
            finally:
                os.close(fd)
            with self.assertRaisesRegex(ValueError,'overlaps'):
                links.plan(plan['originalRoot'],plan['mountRoot'],Path(plan['originalRoot'])/'canary',[binding],'fixture')

    def test_rollback_before_canary_creation_is_a_noop_without_new_directories(self):
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary);plan,_=self.setup_plan(root);journal=runner.Journal(root/'journal','fixture-plan')
            try:
                self.assertEqual(links.rollback_canary(plan,journal),0)
                self.assertFalse((root/'canary').exists())
            finally:journal.close()

    def test_postwrite_source_change_rolls_back_only_new_canary_links(self):
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary);plan,_=self.setup_plan(root);journal=runner.Journal(root/'journal','fixture-plan');calls=[]
            def recheck(proof):
                calls.append(proof)
                if len(calls)==2:
                    raise ValueError('source changed')
            try:
                with self.assertRaisesRegex(ValueError,'source changed'):
                    links.apply_canary(plan,journal,recheck)
                self.assertFalse((root/'canary/folder/fixture.mkv').is_symlink())
                self.assertEqual(os.readlink(Path(plan['originalRoot'])/'folder/fixture.mkv'),'/legacy/fixture')
            finally:
                journal.close()

if __name__=='__main__':unittest.main()
