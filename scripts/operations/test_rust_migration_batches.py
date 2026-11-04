"""Frozen generated schedules and simulated child phases only; no production."""
import importlib.util
from pathlib import Path
import tempfile
import threading
from types import SimpleNamespace
import unittest
from unittest.mock import patch
from test_rust_import_runner import package

SPEC=importlib.util.spec_from_file_location('batches',Path(__file__).with_name('rust-migration-batches.py'))
batches=importlib.util.module_from_spec(SPEC);SPEC.loader.exec_module(batches)

class BatchTests(unittest.TestCase):
    def schedule(self,root):
        sealed=root/'sealed';sealed.mkdir();rows=[]
        for index in range(3):
            checksum=package(sealed/f'batch-{index+1:04d}',lambda value:value.update(batchIndex=index,batchCount=3))
            rows.append({'batchIndex':index,'manifestDigest':checksum})
        value={'schemaVersion':1,'masterManifestDigest':'b'*64,'batchCount':3,'order':[2,0,1],'batches':rows}
        raw=batches.runner.canonical(value);path=root/'schedule.json';path.write_bytes(raw);path.chmod(0o600)
        return batches.Schedule(path,batches.runner.sha(raw),sealed)

    def test_priority_pipeline_resumes_two_slots_and_never_acknowledges_production(self):
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary);schedule=self.schedule(root);calls=[]
            def importing(plan,index,args,state):
                calls.append(('import',index));return {'allCompleted':True,'allTerminal':True}
            def validating(plan,index,args,state):
                calls.append(('validate',index));return {'planDigest':'a'*64,'productionAcknowledged':False}
            with patch.object(batches,'advance_import',importing),patch.object(batches,'validate_batch',validating):
                for _ in range(4):
                    journal=batches.runner.Journal(root/'pipeline.jsonl','fixture-scope')
                    try:
                        result=batches.tick(schedule,SimpleNamespace(),root,journal)
                        self.assertFalse(result['productionAcknowledged']);self.assertFalse(result['originalLibraryChanged'])
                    finally:journal.close()
            self.assertEqual([index for phase,index in calls if phase=='import'],[0,2,1])
            self.assertEqual([index for phase,index in calls if phase=='validate'],[0,2,1])
            self.assertEqual(result['canaryValidated'],3)

    def test_held_validator_does_not_admit_a_third_batch(self):
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary);schedule=self.schedule(root)
            def importing(*args):return {'allCompleted':True,'allTerminal':True}
            def held(*args):raise RuntimeError('fixture unknown source')
            journal=batches.runner.Journal(root/'pipeline.jsonl','fixture-scope')
            try:
                with patch.object(batches,'advance_import',importing),patch.object(batches,'validate_batch',held):
                    for _ in range(4):result=batches.tick(schedule,SimpleNamespace(),root,journal)
                self.assertEqual(result['registered'],2);self.assertEqual(result['held'],1)
                self.assertNotIn('batch:1',journal.latest)
            finally:journal.close()

    def test_import_lane_continues_while_validator_is_busy(self):
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary);schedule=self.schedule(root);advanced=threading.Event();calls=[]
            def importing(*args):
                calls.append(1)
                if len(calls)>1:advanced.set()
                return {'allCompleted':len(calls)>1,'allTerminal':len(calls)>1}
            def validating(*args):
                self.assertTrue(advanced.wait(5),'Import lane did not advance while validation was busy')
                return {'planDigest':'a'*64,'productionAcknowledged':False}
            journal=batches.runner.Journal(root/'pipeline.jsonl','fixture-scope')
            try:
                journal.append('batch:0','validating')
                with patch.object(batches,'advance_import',importing),patch.object(batches,'validate_batch',validating):
                    result=batches.tick(schedule,SimpleNamespace(),root,journal)
                self.assertEqual(len(calls),2);self.assertEqual(result['canaryValidated'],1)
                self.assertNotIn('batch:1',journal.latest)
            finally:journal.close()

    def test_explicit_disposition_needs_known_terminal_acceptance(self):
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary);schedule=self.schedule(root);reason=root/'reason';reason.write_text('Generated failed import reviewed');reason.chmod(0o600)
            journal=batches.runner.Journal(root/'pipeline.jsonl','fixture-scope')
            sealed=schedule.package(0)
            try:
                payload=batches.runner.xml_digest(sealed.payload(sealed.manifest['releases'][0]))
                intent=batches.runner.intent(sealed.digest,'fixture-release')
            finally:sealed.close()
            class Client:
                state='queued'
                def call(self,path):return {'state':self.state,'intent':intent,'payload_sha256':payload,'job_id':'fixture-job'}
            client=Client()
            try:
                journal.append('batch:0','import_held')
                with self.assertRaisesRegex(ValueError,'Active or uncertain'):
                    batches.skip_batch(schedule,0,reason,client,root,journal)
                self.assertEqual(journal.latest['batch:0']['state'],'import_held')
                client.state='failed';result=batches.skip_batch(schedule,0,reason,client,root,journal)
                self.assertFalse(result['productionAcknowledged'])
                self.assertEqual(journal.latest['batch:0']['state'],'skipped_reviewed')
                self.assertEqual(batches.slots(schedule,{0:journal.latest['batch:0']}),(None,2))
            finally:journal.close()

    def test_conflicting_slots_and_changed_packages_are_refused(self):
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary);schedule=self.schedule(root)
            for rows in ({0:{'state':'validating'},1:{'state':'validating'}},
                         {0:{'state':'importing'},1:{'state':'import_held'}},
                         {0:{'state':'unknown'}},{9:{'state':'importing'}}):
                with self.assertRaises(ValueError):batches.slots(schedule,rows)
            (schedule.path(0)/'manifest.json').write_bytes(b'{}')
            with self.assertRaisesRegex(ValueError,'retained receipt'):schedule.package(0)

    def test_owned_state_cannot_replace_a_different_plan_or_overlap_sources(self):
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary);source=root/'source';source.mkdir()
            state=batches.state_root(root/'state','scope-a',[source])
            self.assertEqual(state,batches.state_root(state,'scope-a',[source]))
            with self.assertRaisesRegex(ValueError,'another immutable plan'):batches.state_root(state,'scope-b',[source])
            with self.assertRaisesRegex(ValueError,'overlap'):batches.state_root(source/'state','scope-a',[source])

if __name__=='__main__':unittest.main()
